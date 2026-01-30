// ============================================================================
// TravelPlannerActivities.cs - Durable Functions Activities
// ============================================================================
// Activities are the basic unit of work - they handle storage operations,
// external calls, and streaming progress updates. Each is independently
// retryable by the Durable Functions runtime.
// ============================================================================

using System.Text;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TravelPlannerFunctions.Models;
using TravelPlannerFunctions.Streaming;

namespace TravelPlannerFunctions.Functions;

/// <summary>
/// Activity functions for the travel planner orchestration.
/// </summary>
public class TravelPlannerActivities
{
    private readonly ILogger _logger;
    private readonly BlobServiceClient _blobServiceClient;
    private readonly RedisStreamResponseHandler? _streamHandler;

    public TravelPlannerActivities(
        ILoggerFactory loggerFactory,
        BlobServiceClient blobServiceClient,
        RedisStreamResponseHandler? streamHandler = null)
    {
        _logger = loggerFactory.CreateLogger<TravelPlannerActivities>();
        _blobServiceClient = blobServiceClient ?? throw new ArgumentNullException(nameof(blobServiceClient));
        _streamHandler = streamHandler;
    }

    // =========================================================================
    // Storage Activities
    // =========================================================================

    /// <summary>
    /// Saves a travel plan to Azure Blob Storage.
    /// </summary>
    [Function(nameof(SaveTravelPlanToBlob))]
    public async Task<string> SaveTravelPlanToBlob(
        [ActivityTrigger] SaveTravelPlanRequest request)
    {
        _logger.LogInformation(
            "Saving travel plan for {UserName} to blob storage",
            request.UserName);

        string fileName = $"travel-plan-{request.UserName}-{DateTime.UtcNow:yyyy-MM-dd-HH-mm-ss}.txt";
        var content = FormatTravelPlanAsText(request.TravelPlan, request.UserName);

        var containerClient = _blobServiceClient.GetBlobContainerClient("travel-plans");
        await containerClient.CreateIfNotExistsAsync();

        // Upload the travel plan text to blob storage
        var blobClient = containerClient.GetBlobClient(fileName);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await blobClient.UploadAsync(stream, overwrite: true);

        _logger.LogInformation("Successfully saved travel plan to {BlobUrl}", blobClient.Uri);

        // Return the URL of the uploaded file
        return blobClient.Uri.ToString();
    }

    // =========================================================================
    // Workflow Activities
    // =========================================================================

    /// <summary>
    /// Requests user approval for a travel plan (Human-in-the-Loop).
    /// </summary>
    [Function(nameof(RequestApproval))]
    public ApprovalRequest RequestApproval(
        [ActivityTrigger] ApprovalRequest request)
    {
        _logger.LogInformation(
            "Requesting approval for travel plan for user {UserName}, instance {InstanceId}",
            request.UserName,
            request.InstanceId);

        // In production: send email/SMS/push notification
        _logger.LogInformation("Approval URL: https://your-approval-app/approve?id={InstanceId}", request.InstanceId);

        return request;
    }

    /// <summary>
    /// Books an approved trip (integrates with booking services in production).
    /// </summary>
    [Function(nameof(BookTrip))]
    public async Task<BookingConfirmation> BookTrip(
        [ActivityTrigger] BookingRequest request)
    {
        _logger.LogInformation(
            "Booking trip to {Destination} for user {UserName}",
            request.TravelPlan.Itinerary.DestinationName,
            request.UserName);

        // Simulate API call to booking service
        await Task.Delay(100);

        // Generate booking IDs
        string bookingId = $"TRVL-{Guid.NewGuid().ToString()[..8].ToUpper()}";
        string hotelConfirmation = $"HTL-{Guid.NewGuid().ToString()[..6].ToUpper()}";

        var confirmation = new BookingConfirmation(
            BookingId: bookingId,
            ConfirmationDetails: $"Your trip to {request.TravelPlan.Itinerary.DestinationName} is confirmed for {request.UserName}. Travel dates: {request.TravelPlan.Itinerary.TravelDates}.",
            BookingDate: DateTime.UtcNow,
            HotelConfirmation: hotelConfirmation
        );

        _logger.LogInformation("Trip booked successfully with booking ID {BookingId}", bookingId);
        return confirmation;
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static string FormatTravelPlanAsText(TravelPlan travelPlan, string userName)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"TRAVEL PLAN FOR {userName.ToUpper()}");
        sb.AppendLine($"Generated on {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine(new string('-', 80));
        sb.AppendLine();

        // Add destination information
        var topDestination = travelPlan.DestinationRecommendations.Recommendations
            .OrderByDescending(r => r.MatchScore)
            .FirstOrDefault();

        if (topDestination != null)
        {
            sb.AppendLine("DESTINATION INFORMATION");
            sb.AppendLine("----------------------");
            sb.AppendLine($"Destination: {topDestination.DestinationName}");
            sb.AppendLine($"Match Score: {topDestination.MatchScore}");
            sb.AppendLine($"Description: {topDestination.Description}");
            sb.AppendLine();
        }

        // Add itinerary
        if (travelPlan.Itinerary.DailyPlan.Count > 0)
        {
            sb.AppendLine("ITINERARY");
            sb.AppendLine("---------");
            sb.AppendLine($"Destination: {travelPlan.Itinerary.DestinationName}");
            sb.AppendLine($"Travel Dates: {travelPlan.Itinerary.TravelDates}");
            sb.AppendLine($"Estimated Cost: {travelPlan.Itinerary.EstimatedTotalCost}");
            sb.AppendLine();

            foreach (var day in travelPlan.Itinerary.DailyPlan)
            {
                sb.AppendLine($"DAY {day.Day}: {day.Date}");

                // Format the activities for this day
                foreach (var activity in day.Activities)
                {
                    sb.AppendLine($"  {activity.Time}: {activity.ActivityName}");
                    sb.AppendLine($"      {activity.Description}");
                    sb.AppendLine($"      Location: {activity.Location}");
                    sb.AppendLine($"      Est. Cost: {activity.EstimatedCost}");
                    sb.AppendLine();
                }
            }
        }

        // Add local recommendations
        sb.AppendLine("LOCAL RECOMMENDATIONS");
        sb.AppendLine("--------------------");

        // Add attractions
        sb.AppendLine("Top Attractions:");
        if (travelPlan.LocalRecommendations.Attractions.Count > 0)
        {
            foreach (var attraction in travelPlan.LocalRecommendations.Attractions)
            {
                sb.AppendLine($"- {attraction.Name}: {attraction.Description}");
            }
        }
        else
        {
            sb.AppendLine("No attractions found.");
        }
        sb.AppendLine();

        // Add restaurants
        sb.AppendLine("Recommended Restaurants:");
        if (travelPlan.LocalRecommendations.Restaurants.Count > 0)
        {
            foreach (var restaurant in travelPlan.LocalRecommendations.Restaurants)
            {
                sb.AppendLine($"- {restaurant.Name}: {restaurant.Cuisine} cuisine, {restaurant.PriceRange}");
            }
        }
        else
        {
            sb.AppendLine("No restaurants found.");
        }
        sb.AppendLine();

        // Add additional notes
        if (!string.IsNullOrEmpty(travelPlan.LocalRecommendations.InsiderTips))
        {
            sb.AppendLine("Insider Tips:");
            sb.AppendLine(travelPlan.LocalRecommendations.InsiderTips);
        }

        return sb.ToString();
    }
}