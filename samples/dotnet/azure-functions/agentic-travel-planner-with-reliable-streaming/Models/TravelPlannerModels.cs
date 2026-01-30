// ============================================================================
// TravelPlannerModels.cs - Data Transfer Objects
// ============================================================================

namespace TravelPlannerFunctions.Models;

// ============================================================================
// Request Models
// ============================================================================

public record TravelRequest(
    string UserName,
    string Preferences,
    int DurationInDays,
    string Budget,
    string TravelDates,
    string SpecialRequirements,
    string? ConversationId = null
);

// ============================================================================
// Destination Models
// ============================================================================

public record DestinationRecommendation(
    string DestinationName,
    string Description,
    string Reasoning,
    double MatchScore
);

public record DestinationRecommendations(
    List<DestinationRecommendation> Recommendations
);

// ============================================================================
// Itinerary Models
// ============================================================================

public record ItineraryDay(
    int Day,
    string Date,
    List<ItineraryActivity> Activities
);

public record ItineraryActivity(
    string Time,
    string ActivityName,
    string Description,
    string Location,
    string EstimatedCost
);

public record TravelItinerary(
    string DestinationName,
    string TravelDates,
    List<ItineraryDay> DailyPlan,
    string EstimatedTotalCost,
    string AdditionalNotes
);

// ============================================================================
// Local Recommendations Models
// ============================================================================

public record Attraction(
    string Name,
    string Category,
    string Description,
    string Location,
    string VisitDuration,
    string EstimatedCost,
    double Rating
);

public record Restaurant(
    string Name,
    string Cuisine,
    string Description,
    string Location,
    string PriceRange,
    double Rating
);

public record LocalRecommendations(
    List<Attraction> Attractions,
    List<Restaurant> Restaurants,
    string InsiderTips
);

// ============================================================================
// Composite & Result Models
// ============================================================================

public record TravelPlan(
    DestinationRecommendations DestinationRecommendations,
    TravelItinerary Itinerary,
    LocalRecommendations LocalRecommendations
);

public record SaveTravelPlanRequest(
    TravelPlan TravelPlan,
    string UserName
);

public record TravelPlanResult(
    TravelPlan Plan,
    string? DocumentUrl,
    string? BookingConfirmation = null
);

// ============================================================================
// Approval & Booking Models
// ============================================================================

public record ApprovalRequest(
    string InstanceId,
    TravelPlan TravelPlan,
    string UserName
);

public record ApprovalResponse(
    bool Approved,
    string Comments
);

public record BookingRequest(
    TravelPlan TravelPlan,
    string UserName,
    string ApproverComments
);

public record BookingConfirmation(
    string BookingId,
    string ConfirmationDetails,
    DateTime BookingDate,
    string? HotelConfirmation = null
);

// ============================================================================
// Utility Models
// ============================================================================

public record CurrencyConversion(
    string FromCurrency,
    string ToCurrency,
    decimal OriginalAmount,
    decimal ConvertedAmount,
    decimal ExchangeRate,
    DateTime Timestamp
);

// ============================================================================
// Tool Response Models (for agentic tool results)
// ============================================================================

/// <summary>
/// Result returned when a trip planning orchestration is started.
/// The agent uses this data to inform the user about the planning process.
/// </summary>
public record PlanTripToolResult(
    bool Success,
    string? OrchestrationId,
    string? UserName,
    int? DurationInDays,
    string? Budget,
    string? TravelDates,
    string? Preferences,
    string? SpecialRequirements,
    string? ErrorMessage
);

/// <summary>
/// Detailed travel plan information that the agent can use to answer questions.
/// </summary>
public record TripPlanDetails(
    string OrchestrationId,
    string Status,
    bool IsWaitingForApproval,
    bool IsCompleted,

    // Destination info
    string? DestinationName,
    string? DestinationDescription,
    int? DestinationMatchScore,

    // Itinerary info
    string? TravelDates,
    int? NumberOfDays,
    string? EstimatedTotalCost,
    string? AdditionalNotes,
    List<DayPlanSummary>? DailyPlanSummary,

    // Recommendations
    List<string>? TopAttractions,
    List<string>? TopRestaurants,
    string? InsiderTips,

    // Document
    string? DocumentUrl,

    // Booking (if approved)
    string? BookingConfirmation
);

/// <summary>
/// Summary of a single day in the itinerary.
/// </summary>
public record DayPlanSummary(
    int Day,
    string Date,
    List<string> Activities
);

/// <summary>
/// Result of approving or rejecting a travel plan.
/// </summary>
public record ApprovalToolResult(
    bool Success,
    bool WasApproved,
    string? BookingId,
    string? BookingConfirmation,
    string? ErrorMessage
);

/// <summary>
/// Result from monitoring trip planning progress.
/// Contains the current status and what changed since last check.
/// </summary>
public record MonitoringUpdate(
    string OrchestrationId,
    string Status,
    string? CurrentStep,
    string? PreviousStep,
    int? ProgressPercentage,
    string? SelectedDestination,
    string? StatusMessage,
    bool StepChanged,
    bool IsWaitingForApproval,
    bool IsCompleted,
    bool IsFailed
);
