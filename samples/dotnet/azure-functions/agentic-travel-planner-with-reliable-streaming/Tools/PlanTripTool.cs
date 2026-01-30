// ============================================================================
// PlanTripTool.cs - AI Agent Tools for Trip Planning
// ============================================================================
// Provides tools for the conversational agent to:
// - Start trip planning orchestrations
// - Check orchestration status
// - Retrieve trip plan details (so the agent knows what was planned)
// - Approve or reject travel plans
//
// All tools return structured data - the agent decides how to communicate
// results to the user, making this a true agentic pattern.
// ============================================================================

// Copyright (c) Microsoft. All rights reserved.

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.DurableTask;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using TravelPlannerFunctions.Functions;
using TravelPlannerFunctions.Models;

namespace TravelPlannerFunctions.Tools;

/// <summary>
/// AI agent tools for trip planning orchestration.
/// </summary>
public sealed class PlanTripTool
{
    // Tracks active orchestrations by conversation ID to prevent duplicates
    private static readonly ConcurrentDictionary<string, string> _activeOrchestrations = new();

    public static void TrackOrchestration(string conversationId, string orchestrationId)
    {
        _activeOrchestrations[conversationId] = orchestrationId;
    }

    public static string? GetActiveOrchestration(string conversationId)
    {
        return _activeOrchestrations.TryGetValue(conversationId, out var id) ? id : null;
    }

    private readonly ILogger<PlanTripTool> _logger;

    public PlanTripTool(ILogger<PlanTripTool> logger)
    {
        _logger = logger;
    }

    // =========================================================================
    // Tool: Start Trip Planning
    // =========================================================================

    /// <summary>
    /// Starts the trip planning process by scheduling an orchestration.
    /// </summary>
    [Description(@"Starts the trip planning process. Call this when you have collected all required information from the user.
Returns structured data about the orchestration - use this to inform the user that planning has started.
The orchestration will run in the background, finding destinations, creating itineraries, and gathering recommendations.")]
    public PlanTripToolResult PlanTrip(
        [Description("The traveler's full name")] string userName,
        [Description("Travel preferences (beach, adventure, cultural, relaxation, etc.)")] string preferences,
        [Description("Number of days for the trip (1-30)")] int durationInDays,
        [Description("Budget including currency (e.g., '$5000 USD')")] string budget,
        [Description("Preferred travel dates or date range")] string travelDates,
        [Description("Special requirements (dietary, accessibility, etc.)")] string specialRequirements = "")
    {
        _logger.LogInformation("PlanTrip tool called for user {UserName}", userName);

        // Validate inputs - return structured errors
        if (string.IsNullOrWhiteSpace(userName))
            return new PlanTripToolResult(false, null, null, null, null, null, null, null,
                "Missing user name. Ask the user for their name.");

        if (string.IsNullOrWhiteSpace(preferences))
            return new PlanTripToolResult(false, null, userName, null, null, null, null, null,
                "Missing travel preferences. Ask what kind of trip they want.");

        if (durationInDays < 1 || durationInDays > 30)
            return new PlanTripToolResult(false, null, userName, durationInDays, null, null, null, null,
                "Duration must be between 1 and 30 days.");

        if (string.IsNullOrWhiteSpace(budget))
            return new PlanTripToolResult(false, null, userName, durationInDays, null, null, null, null,
                "Missing budget. Ask the user for their budget.");

        if (string.IsNullOrWhiteSpace(travelDates))
            return new PlanTripToolResult(false, null, userName, durationInDays, budget, null, null, null,
                "Missing travel dates. Ask when they want to travel.");

        try
        {
            string? conversationId = DurableAgentContext.Current?.CurrentThread
                .GetService<AgentThreadMetadata>()?.ConversationId;

            // Check if an orchestration already exists for this conversation
            if (!string.IsNullOrEmpty(conversationId))
            {
                var existingOrchestrationId = GetActiveOrchestration(conversationId);
                if (!string.IsNullOrEmpty(existingOrchestrationId))
                {
                    _logger.LogInformation(
                        "Returning existing orchestration {OrchestrationId} for conversation {ConversationId}",
                        existingOrchestrationId, conversationId);

                    // Return success with existing orchestration - no error, just use MonitorTripPlanning
                    return new PlanTripToolResult(
                        Success: true,
                        OrchestrationId: existingOrchestrationId,
                        UserName: userName,
                        DurationInDays: durationInDays,
                        Budget: budget,
                        TravelDates: travelDates,
                        Preferences: preferences,
                        SpecialRequirements: string.IsNullOrWhiteSpace(specialRequirements) ? null : specialRequirements,
                        ErrorMessage: null
                    );
                }
            }

            var travelRequest = new TravelRequest(
                UserName: userName,
                Preferences: preferences,
                DurationInDays: durationInDays,
                Budget: budget,
                TravelDates: travelDates,
                SpecialRequirements: specialRequirements ?? "",
                ConversationId: conversationId
            );

            string instanceId = DurableAgentContext.Current!.ScheduleNewOrchestration(
                name: nameof(TravelPlannerOrchestrator.RunTravelPlannerOrchestration),
                input: travelRequest);

            // Track this orchestration to prevent duplicates
            if (!string.IsNullOrEmpty(conversationId))
            {
                TrackOrchestration(conversationId, instanceId);
            }

            _logger.LogInformation(
                "Travel planner orchestration started: {InstanceId} for {UserName}",
                instanceId, userName);

            return new PlanTripToolResult(
                Success: true,
                OrchestrationId: instanceId,
                UserName: userName,
                DurationInDays: durationInDays,
                Budget: budget,
                TravelDates: travelDates,
                Preferences: preferences,
                SpecialRequirements: string.IsNullOrWhiteSpace(specialRequirements) ? null : specialRequirements,
                ErrorMessage: null
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start travel planner orchestration");
            return new PlanTripToolResult(false, null, userName, durationInDays, budget, travelDates, preferences, specialRequirements,
                $"Failed to start planning: {ex.Message}");
        }
    }

    // =========================================================================
    // Tool: Monitor Trip Planning Progress
    // =========================================================================

    /// <summary>
    /// Monitors trip planning and waits for the next status change.
    /// </summary>
    [Description(@"Monitors trip planning progress and waits for meaningful status changes.
Call this immediately after PlanTrip to track progress. Keep calling it until IsWaitingForApproval 
or IsCompleted is true. Each call waits up to 10 seconds for a status change before returning.
Use the StatusMessage and StepChanged fields to communicate progress to the user.")]
    public async Task<MonitoringUpdate> MonitorTripPlanning(
        [Description("The orchestration ID to monitor")] string orchestrationId,
        [Description("The last step you saw (pass null on first call)")] string? lastKnownStep = null)
    {
        _logger.LogInformation("Monitoring orchestration: {OrchestrationId}, lastStep: {LastStep}",
            orchestrationId, lastKnownStep ?? "null");

        const int maxWaitSeconds = 10;
        const int pollIntervalMs = 500;
        int iterations = maxWaitSeconds * 1000 / pollIntervalMs;

        string? currentStep = lastKnownStep;
        string? previousStep = lastKnownStep;
        int? progress = null;
        string? destination = null;
        string? statusMessage = null;
        bool stepChanged = false;
        bool isWaitingForApproval = false;
        bool isCompleted = false;
        bool isFailed = false;
        string runtimeStatus = "Unknown";
        int nullCount = 0;

        try
        {
            for (int i = 0; i < iterations; i++)
            {
                var status = await DurableAgentContext.Current!.GetOrchestrationStatusAsync(orchestrationId, true);

                if (status == null)
                {
                    nullCount++;
                    _logger.LogDebug("Monitor iteration {Iteration}: status is null (count: {NullCount})", i, nullCount);

                    // Orchestration may not exist yet - always wait the full time on first call
                    if (lastKnownStep == null)
                    {
                        await Task.Delay(pollIntervalMs);
                        continue;
                    }

                    // For subsequent calls, give it some time but not forever
                    if (i < 5)
                    {
                        await Task.Delay(pollIntervalMs);
                        continue;
                    }
                    return new MonitoringUpdate(
                        orchestrationId, "NotFound", null, lastKnownStep, null, null,
                        "Orchestration not found", false, false, false, true);
                }

                runtimeStatus = status.RuntimeStatus.ToString();
                _logger.LogDebug("Monitor iteration {Iteration}: status = {Status}", i, runtimeStatus);

                isCompleted = status.RuntimeStatus == OrchestrationRuntimeStatus.Completed;
                isFailed = status.RuntimeStatus == OrchestrationRuntimeStatus.Failed;

                // If orchestration is still pending, keep waiting
                if (status.RuntimeStatus == OrchestrationRuntimeStatus.Pending)
                {
                    await Task.Delay(pollIntervalMs);
                    continue;
                }

                if (isCompleted || isFailed)
                {
                    return new MonitoringUpdate(
                        orchestrationId, runtimeStatus, currentStep, previousStep, 100, destination,
                        isCompleted ? "Trip planning completed!" : "Trip planning failed",
                        true, false, isCompleted, isFailed);
                }

                // Parse custom status
                if (status.SerializedCustomStatus != null)
                {
                    try
                    {
                        var customStatus = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                            status.SerializedCustomStatus);

                        if (customStatus != null)
                        {
                            if (customStatus.TryGetValue("step", out var stepEl))
                                currentStep = stepEl.GetString();
                            if (customStatus.TryGetValue("progress", out var progEl))
                                progress = progEl.GetInt32();
                            if (customStatus.TryGetValue("destination", out var destEl))
                                destination = destEl.GetString();
                            if (customStatus.TryGetValue("message", out var msgEl))
                                statusMessage = msgEl.GetString();

                            isWaitingForApproval = currentStep == "WaitingForApproval";
                        }
                    }
                    catch { }
                }

                // Check if step changed
                if (currentStep != lastKnownStep && currentStep != previousStep)
                {
                    stepChanged = true;
                    previousStep = lastKnownStep;

                    // Generate a friendly status message based on the step
                    statusMessage = currentStep switch
                    {
                        "Starting" => "Starting trip planning...",
                        "GetDestinationRecommendations" => "Finding the best destinations for your preferences...",
                        "CreateItineraryAndRecommendations" => destination != null
                            ? $"Creating itinerary for {destination} and finding local recommendations..."
                            : "Creating your itinerary and finding local recommendations...",
                        "SaveTravelPlan" => "Saving your travel plan...",
                        "RequestApproval" => "Preparing your plan for review...",
                        "WaitingForApproval" => "Your travel plan is ready! Please review and let me know if you'd like to approve it.",
                        "BookingTrip" => "Booking your trip...",
                        _ => statusMessage ?? $"Working on: {currentStep}"
                    };

                    return new MonitoringUpdate(
                        orchestrationId, runtimeStatus, currentStep, previousStep, progress, destination,
                        statusMessage, stepChanged, isWaitingForApproval, isCompleted, isFailed);
                }

                await Task.Delay(pollIntervalMs);
            }

            _logger.LogInformation("Monitor completed {Iterations} iterations. Status: {Status}, Step: {Step}, NullCount: {NullCount}",
                iterations, runtimeStatus, currentStep ?? "null", nullCount);

            // If we never got a status and this is initial monitoring, return "starting" not "error"
            if (nullCount == iterations && lastKnownStep == null)
            {
                return new MonitoringUpdate(
                    orchestrationId, "Pending", "Starting", null, 5, null,
                    "Trip planning is initializing. Please continue monitoring.",
                    true, false, false, false);
            }

            // No change detected within timeout - return current state
            return new MonitoringUpdate(
                orchestrationId, runtimeStatus, currentStep ?? "Starting", previousStep, progress ?? 5, destination,
                statusMessage ?? "Trip planning is in progress. Please continue monitoring.",
                currentStep != lastKnownStep, isWaitingForApproval, isCompleted, isFailed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error monitoring orchestration {OrchestrationId}", orchestrationId);
            return new MonitoringUpdate(
                orchestrationId, "Error", currentStep, previousStep, progress, destination,
                $"Error monitoring: {ex.Message}", false, false, false, true);
        }
    }

    // =========================================================================
    // Tool: Get Trip Plan Details
    // =========================================================================

    /// <summary>
    /// Retrieves the full details of a trip plan.
    /// </summary>
    [Description(@"Gets the complete details of a trip plan including destination, itinerary, restaurants, and attractions.
Use this when the user asks questions about their trip plan, wants to know specific details,
or when you need to remind yourself what was planned. This is essential for answering
questions like 'what restaurants did you recommend?' or 'what are we doing on day 2?'")]
    public async Task<TripPlanDetails> GetTripPlanDetails(
        [Description("The orchestration ID returned when planning started")] string orchestrationId)
    {
        _logger.LogInformation("Getting trip plan details for: {OrchestrationId}", orchestrationId);

        try
        {
            var status = await DurableAgentContext.Current!.GetOrchestrationStatusAsync(orchestrationId, true);

            if (status == null)
            {
                return CreateEmptyDetails(orchestrationId, "NotFound");
            }

            var runtimeStatus = status.RuntimeStatus.ToString();
            bool isCompleted = status.RuntimeStatus == OrchestrationRuntimeStatus.Completed;

            // Try to get plan from custom status (available during approval wait)
            if (status.SerializedCustomStatus != null)
            {
                try
                {
                    var details = ParseTripPlanFromCustomStatus(
                        orchestrationId, runtimeStatus, status.SerializedCustomStatus);
                    if (details != null)
                        return details;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse custom status");
                }
            }

            // Try to get plan from output (available after completion)
            if (isCompleted && status.SerializedOutput != null)
            {
                try
                {
                    var details = ParseTripPlanFromOutput(
                        orchestrationId, runtimeStatus, status.SerializedOutput);
                    if (details != null)
                        return details;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse output");
                }
            }

            return CreateEmptyDetails(orchestrationId, runtimeStatus);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get trip plan details for {OrchestrationId}", orchestrationId);
            return CreateEmptyDetails(orchestrationId, "Error");
        }
    }

    private TripPlanDetails? ParseTripPlanFromCustomStatus(
        string orchestrationId, string status, string serializedCustomStatus)
    {
        using var doc = JsonDocument.Parse(serializedCustomStatus);
        var root = doc.RootElement;

        if (!root.TryGetProperty("travelPlan", out var planEl))
            return null;

        var step = root.TryGetProperty("step", out var stepEl) ? stepEl.GetString() : null;
        var isWaitingForApproval = step == "WaitingForApproval";
        var documentUrl = root.TryGetProperty("documentUrl", out var docEl) ? docEl.GetString() : null;

        // Extract destination
        string? destName = null, destDesc = null;
        int? matchScore = null;
        if (planEl.TryGetProperty("destination", out var destEl))
        {
            destName = destEl.GetString();
        }

        // Extract itinerary info
        string? travelDates = null, totalCost = null;
        int? numDays = null;
        List<DayPlanSummary>? dailySummary = null;

        if (planEl.TryGetProperty("dates", out var datesEl))
            travelDates = datesEl.GetString();
        if (planEl.TryGetProperty("cost", out var costEl))
            totalCost = costEl.GetString();
        if (planEl.TryGetProperty("days", out var daysEl))
            numDays = daysEl.GetInt32();

        // Parse daily plan
        if (planEl.TryGetProperty("dailyPlan", out var dailyPlanEl) && dailyPlanEl.ValueKind == JsonValueKind.Array)
        {
            dailySummary = new List<DayPlanSummary>();
            foreach (var dayEl in dailyPlanEl.EnumerateArray())
            {
                var day = dayEl.TryGetProperty("Day", out var dayNumEl) ? dayNumEl.GetInt32() : 0;
                var date = dayEl.TryGetProperty("Date", out var dateEl) ? dateEl.GetString() ?? "" : "";
                var activities = new List<string>();

                if (dayEl.TryGetProperty("Activities", out var activitiesEl) &&
                    activitiesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var actEl in activitiesEl.EnumerateArray())
                    {
                        var time = actEl.TryGetProperty("Time", out var timeEl) ? timeEl.GetString() : "";
                        var name = actEl.TryGetProperty("ActivityName", out var nameEl) ? nameEl.GetString() : "";
                        var cost = actEl.TryGetProperty("EstimatedCost", out var costEl2) ? costEl2.GetString() : "";
                        activities.Add($"{time}: {name} ({cost})");
                    }
                }
                dailySummary.Add(new DayPlanSummary(day, date, activities));
            }
        }

        // Extract recommendations
        List<string>? attractions = null, restaurants = null;
        string? tips = null, additionalNotes = null;

        if (planEl.TryGetProperty("attractions", out var attrEl) && attrEl.ValueKind == JsonValueKind.Object)
        {
            var name = attrEl.TryGetProperty("Name", out var n) ? n.GetString() : null;
            if (name != null) attractions = new List<string> { name };
        }
        if (planEl.TryGetProperty("restaurants", out var restEl) && restEl.ValueKind == JsonValueKind.Object)
        {
            var name = restEl.TryGetProperty("Name", out var n) ? n.GetString() : null;
            if (name != null) restaurants = new List<string> { name };
        }
        if (planEl.TryGetProperty("insiderTips", out var tipsEl))
            tips = tipsEl.GetString();
        if (planEl.TryGetProperty("additionalNotes", out var notesEl))
            additionalNotes = notesEl.GetString();

        return new TripPlanDetails(
            OrchestrationId: orchestrationId,
            Status: status,
            IsWaitingForApproval: isWaitingForApproval,
            IsCompleted: false,
            DestinationName: destName,
            DestinationDescription: destDesc,
            DestinationMatchScore: matchScore,
            TravelDates: travelDates,
            NumberOfDays: numDays,
            EstimatedTotalCost: totalCost,
            AdditionalNotes: additionalNotes,
            DailyPlanSummary: dailySummary,
            TopAttractions: attractions,
            TopRestaurants: restaurants,
            InsiderTips: tips,
            DocumentUrl: documentUrl,
            BookingConfirmation: null
        );
    }

    private TripPlanDetails? ParseTripPlanFromOutput(
        string orchestrationId, string status, string serializedOutput)
    {
        var result = JsonSerializer.Deserialize<TravelPlanResult>(serializedOutput);
        if (result?.Plan == null)
            return null;

        var plan = result.Plan;
        var topDest = plan.DestinationRecommendations.Recommendations
            .OrderByDescending(r => r.MatchScore)
            .FirstOrDefault();

        // Build daily summary
        var dailySummary = plan.Itinerary.DailyPlan.Select(day => new DayPlanSummary(
            day.Day,
            day.Date,
            day.Activities.Select(a => $"{a.Time}: {a.ActivityName} ({a.EstimatedCost})").ToList()
        )).ToList();

        return new TripPlanDetails(
            OrchestrationId: orchestrationId,
            Status: status,
            IsWaitingForApproval: false,
            IsCompleted: true,
            DestinationName: topDest?.DestinationName,
            DestinationDescription: topDest?.Description,
            DestinationMatchScore: topDest != null ? (int)topDest.MatchScore : null,
            TravelDates: plan.Itinerary.TravelDates,
            NumberOfDays: plan.Itinerary.DailyPlan.Count,
            EstimatedTotalCost: plan.Itinerary.EstimatedTotalCost,
            AdditionalNotes: plan.Itinerary.AdditionalNotes,
            DailyPlanSummary: dailySummary,
            TopAttractions: plan.LocalRecommendations.Attractions.Take(5).Select(a => $"{a.Name} ({a.Category})").ToList(),
            TopRestaurants: plan.LocalRecommendations.Restaurants.Take(5).Select(r => $"{r.Name} - {r.Cuisine}").ToList(),
            InsiderTips: plan.LocalRecommendations.InsiderTips,
            DocumentUrl: result.DocumentUrl,
            BookingConfirmation: result.BookingConfirmation
        );
    }

    private static TripPlanDetails CreateEmptyDetails(string orchestrationId, string status)
    {
        return new TripPlanDetails(
            orchestrationId, status, false, false,
            null, null, null, null, null, null, null, null, null, null, null, null, null
        );
    }

    // =========================================================================
    // Tool: Approve or Reject Plan
    // =========================================================================

    /// <summary>
    /// Approves or rejects a travel plan.
    /// </summary>
    [Description(@"Approves or rejects a travel plan that is waiting for user approval.
Call this when the user explicitly says they want to approve/book the trip or reject/decline it.
Returns the booking confirmation if approved successfully.")]
    public async Task<ApprovalToolResult> RespondToTravelPlan(
        [Description("The orchestration ID")] string orchestrationId,
        [Description("True to approve and book, false to reject")] bool approved,
        [Description("Optional user comments")] string comments = "")
    {
        _logger.LogInformation(
            "Processing approval for {OrchestrationId}: Approved={Approved}",
            orchestrationId, approved);

        try
        {
            var approvalResponse = new ApprovalResponse(approved, comments);

            await DurableAgentContext.Current!.RaiseOrchestrationEventAsync(
                orchestrationId,
                "ApprovalEvent",
                approvalResponse);

            if (!approved)
            {
                return new ApprovalToolResult(
                    Success: true,
                    WasApproved: false,
                    BookingId: null,
                    BookingConfirmation: null,
                    ErrorMessage: null
                );
            }

            // Wait for booking to complete
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(1000);
                var status = await DurableAgentContext.Current.GetOrchestrationStatusAsync(orchestrationId, true);

                if (status?.RuntimeStatus == OrchestrationRuntimeStatus.Completed)
                {
                    if (status.SerializedOutput != null)
                    {
                        try
                        {
                            var result = JsonSerializer.Deserialize<TravelPlanResult>(status.SerializedOutput);
                            return new ApprovalToolResult(
                                Success: true,
                                WasApproved: true,
                                BookingId: ExtractBookingId(result?.BookingConfirmation),
                                BookingConfirmation: result?.BookingConfirmation,
                                ErrorMessage: null
                            );
                        }
                        catch { }
                    }

                    return new ApprovalToolResult(true, true, null, "Booking completed", null);
                }
                else if (status?.RuntimeStatus == OrchestrationRuntimeStatus.Failed)
                {
                    return new ApprovalToolResult(false, true, null, null, "Booking failed");
                }
            }

            return new ApprovalToolResult(true, true, null, "Booking in progress", null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process approval for {OrchestrationId}", orchestrationId);
            return new ApprovalToolResult(false, approved, null, null, ex.Message);
        }
    }

    private static string? ExtractBookingId(string? confirmation)
    {
        if (string.IsNullOrEmpty(confirmation)) return null;

        // Try to extract booking ID from confirmation text
        var match = System.Text.RegularExpressions.Regex.Match(
            confirmation, @"TRVL-[A-Z0-9]+");
        return match.Success ? match.Value : null;
    }
}
