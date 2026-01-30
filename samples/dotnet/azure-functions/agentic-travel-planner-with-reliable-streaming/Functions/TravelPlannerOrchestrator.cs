// ============================================================================
// TravelPlannerOrchestrator.cs - Durable Functions Orchestration
// ============================================================================
// Demonstrates:
// - Fan-out/Fan-in: Parallel execution of multiple agents
// - Human-in-the-Loop: Waiting for user approval before booking
// - External Events: Receiving approval/rejection responses
// - Durable Agents: AI-powered agents for specialized tasks
// ============================================================================

using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.DurableTask;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using TravelPlannerFunctions.Models;

namespace TravelPlannerFunctions.Functions;

/// <summary>
/// Orchestrates the complete travel planning workflow using multiple AI agents.
/// </summary>
public class TravelPlannerOrchestrator
{
    // Constants
    private const int ApprovalTimeoutDays = 7;
    private const int StatusMaxSizeBytes = 16_384;

    // Progress percentages for UI
    private const int ProgressStarting = 0;
    private const int ProgressDestinations = 10;
    private const int ProgressItinerary = 30;
    private const int ProgressSavingPlan = 70;
    private const int ProgressRequestingApproval = 85;
    private const int ProgressWaitingForApproval = 90;
    private const int ProgressBooking = 95;

    private readonly ILogger _logger;

    public TravelPlannerOrchestrator(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<TravelPlannerOrchestrator>();
    }

    // =========================================================================
    // Main Orchestration
    // =========================================================================

    /// <summary>
    /// Coordinates the multi-agent travel planning workflow:
    /// 1. Get destination recommendations
    /// 2. Create detailed itinerary (parallel)
    /// 3. Get local recommendations (parallel)
    /// 4. Save travel plan to blob storage
    /// 5. Request user approval (Human-in-the-Loop)
    /// 6. Wait for approval event
    /// 7. If approved, book the trip
    /// </summary>
    [Function(nameof(RunTravelPlannerOrchestration))]
    public async Task<TravelPlanResult> RunTravelPlannerOrchestration(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var travelRequest = context.GetInput<TravelRequest>()
            ?? throw new ArgumentNullException(nameof(context), "Travel request input is required");

        var logger = context.CreateReplaySafeLogger<TravelPlannerOrchestrator>();
        logger.LogInformation(
            "Starting travel planning orchestration for user {UserName}",
            travelRequest.UserName);

        // Set initial status
        SetOrchestrationStatus(context, "Starting",
            $"Starting travel planning for {travelRequest.UserName}",
            ProgressStarting);

        // Initialize AI Agents
        DurableAIAgent destinationAgent = context.GetAgent("DestinationRecommenderAgent");
        DurableAIAgent itineraryAgent = context.GetAgent("ItineraryPlannerAgent");
        DurableAIAgent localRecommendationsAgent = context.GetAgent("LocalRecommendationsAgent");

        AgentThread destinationThread = destinationAgent.GetNewThread();
        AgentThread itineraryThread = itineraryAgent.GetNewThread();
        AgentThread localThread = localRecommendationsAgent.GetNewThread();

        // Step 1: Get Destination Recommendations
        logger.LogInformation("Step 1: Requesting destination recommendations"); ;
        SetOrchestrationStatus(context, "GetDestinationRecommendations",
            "Finding the perfect destinations for your travel preferences...",
            ProgressDestinations);

        var destinationPrompt = $@"Based on the following preferences, recommend 3 travel destinations:
                                User: {travelRequest.UserName}
                                Preferences: {travelRequest.Preferences}
                                Duration: {travelRequest.DurationInDays} days
                                Budget: {travelRequest.Budget}
                                Travel Dates: {travelRequest.TravelDates}
                                Special Requirements: {travelRequest.SpecialRequirements}";

        var destinationResponse = await destinationAgent.RunAsync<DestinationRecommendations>(
            destinationPrompt,
            destinationThread);

        var destinationRecommendations = destinationResponse.Result;

        if (destinationRecommendations.Recommendations.Count == 0)
        {
            logger.LogWarning("No destination recommendations were generated");
            return new TravelPlanResult(CreateEmptyTravelPlan(), string.Empty);
        }

        // For this example, we'll take the top recommendation
        var topDestination = destinationRecommendations.Recommendations
            .OrderByDescending(r => r.MatchScore)
            .First();

        logger.LogInformation("Selected top destination: {DestinationName}", topDestination.DestinationName);

        // Steps 2 & 3: Fan-out/Fan-in - Execute agents in parallel
        logger.LogInformation(
            "Steps 2 & 3: Creating itinerary and getting local recommendations for {DestinationName} in parallel",
            topDestination.DestinationName);
        SetOrchestrationStatus(context, "CreateItineraryAndRecommendations",
            $"Creating a detailed itinerary and finding local gems in {topDestination.DestinationName}...",
            ProgressItinerary, topDestination.DestinationName);

        var itineraryPrompt = $@"Create a {travelRequest.DurationInDays}-day itinerary for {topDestination.DestinationName}.
                            Dates: {travelRequest.TravelDates}
                            Budget: {travelRequest.Budget}
                            Requirements: {travelRequest.SpecialRequirements}
                            
                            CRITICAL: Keep ALL descriptions under 50 characters. Include only 2-4 activities per day.
                            
                            CURRENCY HANDLING:
                            1. Determine the user's budget currency from the budget string above (e.g., '$5000' = USD, '€3000' = EUR)
                            2. Determine the local currency for {topDestination.DestinationName}
                            3. Call GetExchangeRate with the user's currency and the local currency
                            4. Show costs as: '<local_amount> <LOCAL_CODE> (<user_amount> <USER_CODE>)'
                            5. Include the exchange rate in AdditionalNotes";

        var localPrompt = $@"Provide local recommendations for {topDestination.DestinationName}:
                        Duration: {travelRequest.DurationInDays} days
                        Preferred Cuisine: Any
                        Include Hidden Gems: true
                        Family Friendly: {travelRequest.SpecialRequirements.Contains("family", StringComparison.OrdinalIgnoreCase)}";

        // Execute both agent calls in parallel
        logger.LogInformation("Calling itinerary agent with prompt length: {Length}", itineraryPrompt.Length);
        var itineraryTask = itineraryAgent.RunAsync<TravelItinerary>(
            itineraryPrompt,
            itineraryThread);

        logger.LogInformation("Calling local recommendations agent");
        var localRecommendationsTask = localRecommendationsAgent.RunAsync<LocalRecommendations>(
            localPrompt,
            localThread);

        // Wait for both tasks to complete
        await Task.WhenAll(itineraryTask, localRecommendationsTask);

        var itineraryResponse = await itineraryTask;
        var localRecommendationsResponse = await localRecommendationsTask;

        // Validate and correct the cost calculation
        var itinerary = ValidateAndFixCostCalculation(itineraryResponse.Result, logger);
        var localRecommendations = localRecommendationsResponse.Result;

        // Combine results into a travel plan
        var travelPlan = new TravelPlan(destinationRecommendations, itinerary, localRecommendations);

        // Step 4: Save Travel Plan to Blob Storage
        logger.LogInformation("Step 4: Saving travel plan to blob storage");
        SetOrchestrationStatus(context, "SaveTravelPlan",
            "Finalizing your travel plan and preparing documentation...",
            ProgressSavingPlan, topDestination.DestinationName);
        var savePlanRequest = new SaveTravelPlanRequest(travelPlan, travelRequest.UserName);
        string? documentUrl;
        {
            documentUrl = await context.CallActivityAsync<string>(
                nameof(TravelPlannerActivities.SaveTravelPlanToBlob),
                savePlanRequest);

            if (string.IsNullOrEmpty(documentUrl))
            {
                logger.LogWarning("Failed to save travel plan to blob storage");
                documentUrl = null;
            }

            // Step 5: Request User Approval (Human-in-the-Loop)
            logger.LogInformation("Step 5: Requesting approval for travel plan");
            SetOrchestrationStatus(context, "RequestApproval",
                "Sending travel plan for your approval...",
                ProgressRequestingApproval, topDestination.DestinationName, documentUrl);
            var approvalRequest = new ApprovalRequest(context.InstanceId, travelPlan, travelRequest.UserName);
            await context.CallActivityAsync(nameof(TravelPlannerActivities.RequestApproval), approvalRequest);

            // Step 6: Wait for External Event (User Approval)
            logger.LogInformation(
                "Step 6: Waiting for approval from user {UserName}",
                travelRequest.UserName);

            // Wait for external event with timeout
            ApprovalResponse approvalResponse;
            try
            {
                SetApprovalWaitingStatus(context, topDestination.DestinationName, documentUrl,
                    itinerary, localRecommendations, logger);

                approvalResponse = await context.WaitForExternalEvent<ApprovalResponse>(
                    "ApprovalEvent",
                    TimeSpan.FromDays(ApprovalTimeoutDays));
            }
            catch (TaskCanceledException)
            {
                // If timeout occurs, use the default response
                logger.LogWarning("Approval request timed out for user {UserName}", travelRequest.UserName);
                approvalResponse = new ApprovalResponse(false, "Timed out waiting for approval");
            }

            // Check if the trip was approved
            if (approvalResponse.Approved)
            {
                // Step 7: Book the Trip
                logger.LogInformation(
                    "Step 7: Booking trip to {Destination} for user {UserName}",
                    itinerary.DestinationName,
                    travelRequest.UserName);

                SetOrchestrationStatus(context, "BookingTrip",
                    $"Booking your trip to {topDestination.DestinationName}...",
                    ProgressBooking, topDestination.DestinationName, documentUrl, approved: true);

                var bookingRequest = new BookingRequest(travelPlan, travelRequest.UserName, approvalResponse.Comments);
                var bookingConfirmation = await context.CallActivityAsync<BookingConfirmation>(
                    nameof(TravelPlannerActivities.BookTrip), bookingRequest);

                // Return the travel plan with booking confirmation
                logger.LogInformation("Completed travel planning for {UserName} with booking confirmation {BookingId}",
                    travelRequest.UserName, bookingConfirmation.BookingId);

                // Format rich booking confirmation
                var confirmationText = FormatBookingConfirmation(bookingConfirmation, travelRequest.UserName);

                return new TravelPlanResult(
                    travelPlan,
                    documentUrl,
                    confirmationText);
            }
            else
            {
                // Return the travel plan without booking
                logger.LogInformation("Travel plan for {UserName} was not approved. Comments: {Comments}",
                    travelRequest.UserName, approvalResponse.Comments);

                return new TravelPlanResult(
                    travelPlan,
                    documentUrl,
                    $"Travel plan was not approved. Comments: {approvalResponse.Comments}");
            }
        }
    }

    // =========================================================================
    // Helper Methods
    // =========================================================================

    private TravelPlan CreateEmptyTravelPlan()
    {
        return new TravelPlan(
            new DestinationRecommendations(new List<DestinationRecommendation>()),
            new TravelItinerary("None", "N/A", new List<ItineraryDay>(), "0", "No itinerary available"),
            new LocalRecommendations(new List<Attraction>(), new List<Restaurant>(), "No recommendations available")
        );
    }

    private TravelItinerary ValidateAndFixCostCalculation(TravelItinerary itinerary, ILogger logger)
    {
        try
        {
            // Extract all activity costs and sum them up
            decimal totalCost = 0;
            string? localCurrency = null;
            string? userCurrency = null;
            decimal exchangeRate = 1.0m;

            foreach (var day in itinerary.DailyPlan)
            {
                foreach (var activity in day.Activities)
                {
                    var cost = activity.EstimatedCost;
                    if (string.IsNullOrEmpty(cost) ||
                        cost.Equals("Free", StringComparison.OrdinalIgnoreCase) ||
                        cost.Equals("Varies", StringComparison.OrdinalIgnoreCase) ||
                        cost.Equals("0", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Parse cost string like "120,000 IDR (7 USD)" or "500 JPY (3.26 USD)" or "25 EUR"
                    // The regex now handles numbers with commas
                    var match = System.Text.RegularExpressions.Regex.Match(cost, @"([\d,]+(?:\.\d+)?)\s*([A-Z]{3})");
                    if (match.Success)
                    {
                        // Remove commas before parsing
                        var amountStr = match.Groups[1].Value.Replace(",", "");
                        if (decimal.TryParse(amountStr, out decimal amount))
                        {
                            totalCost += amount;
                            if (localCurrency == null)
                            {
                                localCurrency = match.Groups[2].Value;
                            }
                        }

                        // Try to extract the converted currency for exchange rate calculation
                        var convertedMatch = System.Text.RegularExpressions.Regex.Match(cost, @"\(([\d,]+(?:\.\d+)?)\s*([A-Z]{3})\)");
                        if (convertedMatch.Success && userCurrency == null)
                        {
                            userCurrency = convertedMatch.Groups[2].Value;
                            var convertedAmountStr = convertedMatch.Groups[1].Value.Replace(",", "");
                            if (decimal.TryParse(convertedAmountStr, out decimal convertedAmount) && amount > 0)
                            {
                                exchangeRate = convertedAmount / amount;
                            }
                        }
                    }
                }
            }

            // Calculate the corrected total cost
            var correctedLocalCost = Math.Round(totalCost, 2);
            var correctedUserCost = Math.Round(totalCost * exchangeRate, 2);

            // If our calculation failed (0), keep the agent's original value
            if (correctedLocalCost == 0)
            {
                logger.LogInformation("Cost validation returned 0, keeping agent's original value: {AgentCost}",
                    itinerary.EstimatedTotalCost);
                return itinerary;
            }

            // Format the corrected cost string
            string correctedCostString;
            if (!string.IsNullOrEmpty(userCurrency) && localCurrency != userCurrency)
            {
                correctedCostString = $"{correctedLocalCost:N0} {localCurrency} ({correctedUserCost:N2} {userCurrency})";
            }
            else
            {
                correctedCostString = $"{correctedLocalCost:N0} {localCurrency ?? "USD"}";
            }

            // Log the correction if there was a discrepancy
            if (itinerary.EstimatedTotalCost != correctedCostString)
            {
                logger.LogWarning(
                    "Cost calculation corrected. Agent calculated: {AgentCost}, Actual sum: {CorrectedCost}",
                    itinerary.EstimatedTotalCost,
                    correctedCostString);
            }

            // Return a new itinerary with the corrected cost
            return new TravelItinerary(
                itinerary.DestinationName,
                itinerary.TravelDates,
                itinerary.DailyPlan,
                correctedCostString,
                itinerary.AdditionalNotes
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error validating cost calculation, using agent's original value");
            return itinerary;
        }
    }

    private void SetOrchestrationStatus(
        TaskOrchestrationContext context,
        string step,
        string message,
        int progress,
        string? destination = null,
        string? documentUrl = null,
        bool? approved = null)
    {
        var status = new Dictionary<string, object>
        {
            ["step"] = step,
            ["message"] = message,
            ["progress"] = progress
        };

        if (destination != null) status["destination"] = destination;
        if (documentUrl != null) status["documentUrl"] = documentUrl;
        if (approved.HasValue) status["approved"] = approved.Value;

        context.SetCustomStatus(status);
    }

    private void SetApprovalWaitingStatus(
        TaskOrchestrationContext context,
        string destinationName,
        string? documentUrl,
        TravelItinerary itinerary,
        LocalRecommendations localRecommendations,
        ILogger logger)
    {
        var waitingStatus = new
        {
            step = "WaitingForApproval",
            message = "Waiting for your approval of the travel plan...",
            progress = ProgressWaitingForApproval,
            destination = destinationName,
            documentUrl = documentUrl,
            travelPlan = new
            {
                destination = destinationName,
                dates = itinerary.TravelDates,
                cost = itinerary.EstimatedTotalCost,
                days = itinerary.DailyPlan.Count,
                dailyPlan = itinerary.DailyPlan,
                additionalNotes = itinerary.AdditionalNotes,
                attractions = localRecommendations.Attractions.FirstOrDefault(),
                restaurants = localRecommendations.Restaurants.FirstOrDefault(),
                insiderTips = localRecommendations.InsiderTips
            }
        };

        var serialized = JsonSerializer.Serialize(waitingStatus);
        var statusSize = Encoding.Unicode.GetByteCount(serialized);

        if (statusSize > StatusMaxSizeBytes)
        {
            logger.LogWarning("Waiting status size ({Size} bytes) exceeds maximum ({MaxSize} bytes). Status may be truncated.",
                statusSize, StatusMaxSizeBytes);
        }
        else
        {
            logger.LogInformation("Waiting status size: {Size} bytes", statusSize);
        }

        context.SetCustomStatus(waitingStatus);
    }

    /// <summary>
    /// Formats a booking confirmation with hotel details.
    /// </summary>
    private static string FormatBookingConfirmation(BookingConfirmation confirmation, string userName)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"🎉 **Booking Confirmed!**");
        sb.AppendLine();
        sb.AppendLine($"**Confirmation Number:** `{confirmation.BookingId}`");
        sb.AppendLine($"**Traveler:** {userName}");
        sb.AppendLine($"**Booking Date:** {confirmation.BookingDate:MMMM d, yyyy}");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(confirmation.HotelConfirmation))
        {
            sb.AppendLine($"🏨 **Hotel Confirmation:** `{confirmation.HotelConfirmation}`");
            sb.AppendLine();
        }

        sb.AppendLine(confirmation.ConfirmationDetails);

        return sb.ToString();
    }

}