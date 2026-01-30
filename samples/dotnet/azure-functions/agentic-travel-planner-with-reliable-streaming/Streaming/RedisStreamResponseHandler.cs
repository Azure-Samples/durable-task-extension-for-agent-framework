// ============================================================================
// RedisStreamResponseHandler.cs - Reliable Streaming via Redis Streams
// ============================================================================
// Enables reliable delivery of AI agent responses:
// - Clients can disconnect and reconnect without losing messages
// - Cursor-based resumption from any point in the stream
// - Automatic TTL-based cleanup of old streams
// ============================================================================

// Copyright (c) Microsoft. All rights reserved.

using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.DurableTask;
using StackExchange.Redis;
using TravelPlannerFunctions.Tools;

namespace TravelPlannerFunctions.Streaming;

// ============================================================================
// Data Transfer Objects
// ============================================================================

/// <summary>
/// Represents a chunk of data read from a Redis stream.
/// </summary>
public readonly record struct StreamChunk(
    string EntryId,
    string? Text,
    bool IsDone,
    string? Error
);

// ============================================================================
// Response Handler Implementation
// ============================================================================

/// <summary>
/// Publishes agent response updates to Redis Streams for reliable delivery.
/// </summary>
public sealed class RedisStreamResponseHandler : IAgentResponseHandler
{
    private const int MaxEmptyReads = 300;  // 5 minutes at 1s intervals
    private const int PollIntervalMs = 1000;

    private readonly IConnectionMultiplexer _redis;
    private readonly TimeSpan _streamTtl;

    public RedisStreamResponseHandler(IConnectionMultiplexer redis, TimeSpan streamTtl)
    {
        _redis = redis;
        _streamTtl = streamTtl;
    }

    // =========================================================================
    // IAgentResponseHandler Implementation
    // =========================================================================

    /// <inheritdoc/>
    public async ValueTask OnStreamingResponseUpdateAsync(
        IAsyncEnumerable<AgentRunResponseUpdate> messageStream,
        CancellationToken cancellationToken)
    {
        DurableAgentContext? context = DurableAgentContext.Current
            ?? throw new InvalidOperationException(
                "DurableAgentContext.Current is not set.");

        string conversationId = context.CurrentThread.GetService<AgentThreadMetadata>()?.ConversationId
            ?? throw new InvalidOperationException("Unable to determine conversation ID.");
        string streamKey = GetStreamKey(conversationId);

        IDatabase db = _redis.GetDatabase();
        int sequenceNumber = 0;

        await foreach (AgentRunResponseUpdate update in messageStream.WithCancellation(cancellationToken))
        {
            string text = update.Text;

            // Only publish non-empty text chunks
            if (!string.IsNullOrEmpty(text))
            {
                // Create the stream entry with the text and metadata
                NameValueEntry[] entries =
                [
                    new NameValueEntry("text", text),
                    new NameValueEntry("sequence", sequenceNumber++),
                    new NameValueEntry("timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                ];

                // Add to the Redis Stream with auto-generated ID (timestamp-based)
                await db.StreamAddAsync(streamKey, entries);

                // Refresh the TTL on each write to keep the stream alive during active streaming
                await db.KeyExpireAsync(streamKey, _streamTtl);
            }
        }

        // Always send done marker to close the stream
        // (Orchestration progress is now monitored via agent tools, not streaming)
        NameValueEntry[] endEntries =
        [
            new NameValueEntry("text", ""),
            new NameValueEntry("sequence", sequenceNumber),
            new NameValueEntry("timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            new NameValueEntry("done", "true"),
        ];
        await db.StreamAddAsync(streamKey, endEntries);

        // Set final TTL - the stream will be cleaned up after this duration
        await db.KeyExpireAsync(streamKey, _streamTtl);
    }

    /// <inheritdoc/>
    public async ValueTask OnAgentResponseAsync(AgentRunResponse message, CancellationToken cancellationToken)
    {
        // Handle non-streaming responses
        DurableAgentContext? context = DurableAgentContext.Current;
        if (context is null)
        {
            return; // Can't write without context
        }

        string? conversationId = context.CurrentThread.GetService<AgentThreadMetadata>()?.ConversationId;
        if (string.IsNullOrEmpty(conversationId))
        {
            return;
        }

        string streamKey = GetStreamKey(conversationId);
        IDatabase db = _redis.GetDatabase();

        // Write the full response text
        string? responseText = message.Text;
        if (!string.IsNullOrEmpty(responseText))
        {
            NameValueEntry[] entries =
            [
                new NameValueEntry("text", responseText),
                new NameValueEntry("sequence", 0),
                new NameValueEntry("timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            ];
            await db.StreamAddAsync(streamKey, entries);
        }

        // Always send done marker to close the stream
        NameValueEntry[] endEntries =
        [
            new NameValueEntry("text", ""),
            new NameValueEntry("sequence", 1),
            new NameValueEntry("timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            new NameValueEntry("done", "true"),
        ];
        await db.StreamAddAsync(streamKey, endEntries);
        await db.KeyExpireAsync(streamKey, _streamTtl);
    }

    // =========================================================================
    // Stream Reading
    // =========================================================================

    /// <summary>
    /// Reads chunks from a Redis stream, yielding them as they become available.
    /// </summary>
    public async IAsyncEnumerable<StreamChunk> ReadStreamAsync(
        string conversationId,
        string? cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string streamKey = GetStreamKey(conversationId);

        IDatabase db = _redis.GetDatabase();
        string startId = string.IsNullOrEmpty(cursor) ? "0-0" : cursor;

        int emptyReadCount = 0;
        bool hasSeenData = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            StreamEntry[]? entries = null;
            string? errorMessage = null;

            try
            {
                entries = await db.StreamReadAsync(streamKey, startId, count: 100);
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
            }

            if (errorMessage != null)
            {
                yield return new StreamChunk(startId, null, false, errorMessage);
                yield break;
            }

            // entries is guaranteed to be non-null if errorMessage is null
            if (entries!.Length == 0)
            {
                if (!hasSeenData)
                {
                    emptyReadCount++;
                    if (emptyReadCount >= MaxEmptyReads)
                    {
                        yield return new StreamChunk(
                            startId,
                            null,
                            false,
                            $"Stream not found or timed out after {MaxEmptyReads * PollIntervalMs / 1000} seconds");
                        yield break;
                    }
                }

                await Task.Delay(PollIntervalMs, cancellationToken);
                continue;
            }

            hasSeenData = true;

            foreach (StreamEntry entry in entries)
            {
                startId = entry.Id.ToString();
                string? text = entry["text"];
                string? done = entry["done"];

                if (done == "true")
                {
                    yield return new StreamChunk(startId, null, true, null);
                    yield break;
                }

                if (!string.IsNullOrEmpty(text))
                {
                    yield return new StreamChunk(startId, text, false, null);
                }
            }
        }
    }

    // =========================================================================
    // Stream Management
    // =========================================================================

    /// <summary>
    /// Clears the Redis stream for a conversation.
    /// </summary>
    public async Task ClearStreamAsync(string conversationId)
    {
        string streamKey = GetStreamKey(conversationId);
        IDatabase db = _redis.GetDatabase();
        await db.KeyDeleteAsync(streamKey);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    internal static string GetStreamKey(string conversationId) => $"agent-stream:{conversationId}";
}
