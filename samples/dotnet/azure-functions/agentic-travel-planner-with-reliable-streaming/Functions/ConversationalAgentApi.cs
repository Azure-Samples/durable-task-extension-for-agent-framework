// ============================================================================
// ConversationalAgentApi.cs - Chat API with Reliable Streaming
// ============================================================================
// Endpoints:
// - POST /api/agent/create        Start a new conversation
// - POST /api/agent/chat/{id}     Continue existing conversation  
// - GET  /api/agent/stream/{id}   Resume stream from cursor
// - GET  /api/agent/orchestration/{id}        Get orchestration status
// - POST /api/agent/orchestration/{id}/approve Approve/reject plan
// ============================================================================

// Copyright (c) Microsoft. All rights reserved.

using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.DurableTask;
using Microsoft.Agents.AI.Hosting.AzureFunctions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using TravelPlannerFunctions.Streaming;

namespace TravelPlannerFunctions.Functions;

/// <summary>
/// HTTP trigger functions for the conversational travel planner agent.
/// </summary>
public sealed class ConversationalAgentApi
{
    private readonly RedisStreamResponseHandler _streamHandler;
    private readonly ILogger<ConversationalAgentApi> _logger;

    public ConversationalAgentApi(
        RedisStreamResponseHandler streamHandler,
        ILogger<ConversationalAgentApi> logger)
    {
        _streamHandler = streamHandler;
        _logger = logger;
    }

    // =========================================================================
    // Conversation Endpoints
    // =========================================================================

    /// <summary>
    /// Creates a new conversation with the travel planner agent.
    /// </summary>
    [Function("CreateConversation")]
    public async Task<IActionResult> CreateConversationAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "agent/create")] HttpRequest request,
        [DurableClient] DurableTaskClient durableClient,
        FunctionContext context,
        CancellationToken cancellationToken)
    {
        // Read the prompt from the request body
        string prompt = await new StreamReader(request.Body).ReadToEndAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new BadRequestObjectResult("Request body must contain a message.");
        }

        AIAgent agentProxy = durableClient.AsDurableAgentProxy(context, "ConversationalTravelAgent");

        // Create a new agent thread
        AgentThread thread = agentProxy.GetNewThread();
        AgentThreadMetadata metadata = thread.GetService<AgentThreadMetadata>()
            ?? throw new InvalidOperationException("Failed to get AgentThreadMetadata from new thread.");

        _logger.LogInformation("Creating new conversation: {ConversationId}", metadata.ConversationId);

        // Clear any existing stream data before starting (in case of retry/reconnect)
        await _streamHandler.ClearStreamAsync(metadata.ConversationId!);

        // Run the agent - tools use DurableAgentContext.Current to schedule orchestrations
        DurableAgentRunOptions options = new() { IsFireAndForget = true };
        await agentProxy.RunAsync(prompt, thread, options, cancellationToken);

        _logger.LogInformation("Agent run started for conversation: {ConversationId}", metadata.ConversationId);

        // Check Accept header to determine response format
        string? acceptHeader = request.Headers.Accept.FirstOrDefault();
        bool useSseFormat = acceptHeader?.Contains("text/plain", StringComparison.OrdinalIgnoreCase) != true;

        return await StreamToClientAsync(
            conversationId: metadata.ConversationId!,
            cursor: null,
            useSseFormat,
            request.HttpContext,
            cancellationToken);
    }

    /// <summary>
    /// Sends a follow-up message to an existing conversation.
    /// </summary>
    [Function("ChatWithAgent")]
    public async Task<IActionResult> ChatWithAgentAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "agent/chat/{conversationId}")] HttpRequest request,
        string conversationId,
        [DurableClient] DurableTaskClient durableClient,
        FunctionContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return new BadRequestObjectResult("Conversation ID is required.");
        }

        string message = await new StreamReader(request.Body).ReadToEndAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(message))
        {
            return new BadRequestObjectResult("Request body must contain a message.");
        }

        _logger.LogInformation("Continuing conversation {ConversationId}", conversationId);

        // Clear the stream before the new response
        await _streamHandler.ClearStreamAsync(conversationId);

        AIAgent agentProxy = durableClient.AsDurableAgentProxy(context, "ConversationalTravelAgent");

        // Reconstruct the existing thread from the conversation ID
        string threadJson = $"{{\"sessionId\":\"{conversationId}\"}}";
        using JsonDocument doc = JsonDocument.Parse(threadJson);
        AgentThread thread = agentProxy.DeserializeThread(doc.RootElement);

        // Run the agent
        DurableAgentRunOptions options = new() { IsFireAndForget = true };
        await agentProxy.RunAsync(message, thread, options, cancellationToken);

        _logger.LogInformation("Agent run continued for conversation: {ConversationId}", conversationId);

        // Check Accept header to determine response format
        string? acceptHeader = request.Headers.Accept.FirstOrDefault();
        bool useSseFormat = acceptHeader?.Contains("text/plain", StringComparison.OrdinalIgnoreCase) != true;

        // Stream from the same conversation ID - the entity state is preserved
        return await StreamToClientAsync(
            conversationId,
            cursor: null,
            useSseFormat,
            request.HttpContext,
            cancellationToken);
    }

    // =========================================================================
    // Stream Resumption
    // =========================================================================

    /// <summary>
    /// Resumes streaming from a specific cursor position.
    /// </summary>
    [Function("StreamConversation")]
    public async Task<IActionResult> StreamConversationAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "agent/stream/{conversationId}")] HttpRequest request,
        string conversationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return new BadRequestObjectResult("Conversation ID is required.");
        }

        // Get the cursor from query string (optional)
        string? cursor = request.Query["cursor"].FirstOrDefault();

        _logger.LogInformation("Resuming stream for conversation {ConversationId}", conversationId);

        // Check Accept header to determine response format
        string? acceptHeader = request.Headers.Accept.FirstOrDefault();
        bool useSseFormat = acceptHeader?.Contains("text/plain", StringComparison.OrdinalIgnoreCase) != true;

        return await StreamToClientAsync(conversationId, cursor, useSseFormat, request.HttpContext, cancellationToken);
    }

    // =========================================================================
    // Orchestration Status & Approval
    // =========================================================================

    /// <summary>
    /// Gets the status of an orchestration.
    /// </summary>
    [Function("GetOrchestrationStatus")]
    public async Task<IActionResult> GetOrchestrationStatusAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "agent/orchestration/{instanceId}")] HttpRequest request,
        string instanceId,
        [DurableClient] DurableTaskClient client,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Getting orchestration status for instance: {InstanceId}", instanceId);

        var status = await client.GetInstanceAsync(instanceId, true);

        if (status == null)
        {
            return new NotFoundObjectResult($"No orchestration found with ID = {instanceId}");
        }

        return new OkObjectResult(status);
    }

    /// <summary>
    /// Handles approval response for an orchestration.
    /// </summary>
    [Function("HandleOrchestrationApproval")]
    public async Task<IActionResult> HandleOrchestrationApprovalAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "agent/orchestration/{instanceId}/approve")] HttpRequest request,
        string instanceId,
        [DurableClient] DurableTaskClient client,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Handling approval for orchestration: {InstanceId}", instanceId);

        var approvalResponse = await request.ReadFromJsonAsync<Models.ApprovalResponse>(cancellationToken);
        if (approvalResponse == null)
        {
            return new BadRequestObjectResult("Invalid approval response.");
        }

        await client.RaiseEventAsync(instanceId, "ApprovalEvent", approvalResponse);

        return new OkObjectResult(new
        {
            instanceId,
            message = "Approval response processed successfully",
            approved = approvalResponse.Approved
        });
    }

    // =========================================================================
    // Streaming Helpers
    // =========================================================================

    /// <summary>
    /// Streams chunks from the Redis stream to the HTTP response.
    /// </summary>
    private async Task<IActionResult> StreamToClientAsync(
        string conversationId,
        string? cursor,
        bool useSseFormat,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // Set response headers based on format
        httpContext.Response.Headers.ContentType = useSseFormat
            ? "text/event-stream"
            : "text/plain; charset=utf-8";
        httpContext.Response.Headers.CacheControl = "no-cache";
        httpContext.Response.Headers.Connection = "keep-alive";
        httpContext.Response.Headers["x-conversation-id"] = conversationId;

        // Expose custom header for CORS (Azure Functions built-in CORS doesn't support this)
        httpContext.Response.Headers["Access-Control-Expose-Headers"] = "x-conversation-id";

        // Disable response buffering if supported
        httpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        try
        {
            await foreach (StreamChunk chunk in _streamHandler.ReadStreamAsync(
                conversationId,
                cursor,
                cancellationToken))
            {
                if (chunk.Error != null)
                {
                    _logger.LogWarning("Stream error for conversation {ConversationId}: {Error}", conversationId, chunk.Error);
                    await WriteErrorAsync(httpContext.Response, chunk.Error, useSseFormat, cancellationToken);
                    break;
                }

                if (chunk.IsDone)
                {
                    await WriteEndOfStreamAsync(httpContext.Response, chunk.EntryId, useSseFormat, cancellationToken);
                    break;
                }

                if (chunk.Text != null)
                {
                    await WriteChunkAsync(httpContext.Response, chunk, useSseFormat, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Client disconnected from stream {ConversationId}", conversationId);
        }

        return new EmptyResult();
    }

    /// <summary>
    /// Writes a text chunk to the response.
    /// </summary>
    private static async Task WriteChunkAsync(
        HttpResponse response,
        StreamChunk chunk,
        bool useSseFormat,
        CancellationToken cancellationToken)
    {
        if (useSseFormat)
        {
            await WriteSSEEventAsync(response, "message", chunk.Text!, chunk.EntryId);
        }
        else
        {
            await response.WriteAsync(chunk.Text!, cancellationToken);
        }

        await response.Body.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Writes an end-of-stream marker to the response.
    /// </summary>
    private static async Task WriteEndOfStreamAsync(
        HttpResponse response,
        string entryId,
        bool useSseFormat,
        CancellationToken cancellationToken)
    {
        if (useSseFormat)
        {
            await WriteSSEEventAsync(response, "done", "[DONE]", entryId);
        }
        else
        {
            await response.WriteAsync("\n", cancellationToken);
        }

        await response.Body.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Writes an error message to the response.
    /// </summary>
    private static async Task WriteErrorAsync(
        HttpResponse response,
        string error,
        bool useSseFormat,
        CancellationToken cancellationToken)
    {
        if (useSseFormat)
        {
            await WriteSSEEventAsync(response, "error", error, null);
        }
        else
        {
            await response.WriteAsync($"\n[Error: {error}]\n", cancellationToken);
        }

        await response.Body.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Writes a Server-Sent Event to the response stream.
    /// </summary>
    private static async Task WriteSSEEventAsync(
        HttpResponse response,
        string eventType,
        string data,
        string? id)
    {
        StringBuilder sb = new();

        // Include the ID if provided (used as cursor for resumption)
        if (!string.IsNullOrEmpty(id))
        {
            sb.AppendLine($"id: {id}");
        }

        sb.AppendLine($"event: {eventType}");

        // SSE data field must handle multi-line content:
        // Each line must be prefixed with "data: "
        foreach (var line in data.Split('\n'))
        {
            sb.AppendLine($"data: {line}");
        }

        sb.AppendLine(); // Empty line marks end of event

        await response.WriteAsync(sb.ToString());
    }
}
