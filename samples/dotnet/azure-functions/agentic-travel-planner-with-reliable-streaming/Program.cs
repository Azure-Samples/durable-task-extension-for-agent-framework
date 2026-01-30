// =============================================================================
// AGENTIC TRAVEL PLANNER - Azure Functions + Durable Agents Sample
// =============================================================================
// This sample demonstrates a multi-agent travel planning application using:
//   - Azure Durable Functions for orchestration
//   - Azure OpenAI for AI capabilities
//   - Redis Streams for reliable response delivery
//   - Human-in-the-loop approval workflows
// =============================================================================

using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.DurableTask;
using Microsoft.Agents.AI.Hosting.AzureFunctions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.StackExchangeRedis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using TravelPlannerFunctions.Streaming;
using TravelPlannerFunctions.Tools;

// =============================================================================
// CONFIGURATION
// =============================================================================

// Azure OpenAI settings (required)
string openAiEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
    ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT environment variable is required.");
string openAiDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME")
    ?? throw new InvalidOperationException("AZURE_OPENAI_DEPLOYMENT_NAME environment variable is required.");

// Redis settings (for reliable streaming)
string? redisConnectionString = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING");
string? redisHostName = Environment.GetEnvironmentVariable("REDIS_HOST_NAME");
string? redisSslPort = Environment.GetEnvironmentVariable("REDIS_SSL_PORT");
bool useRedisManagedIdentity = Environment.GetEnvironmentVariable("REDIS_USE_MANAGED_IDENTITY")?.ToLower() == "true";
int redisStreamTtlMinutes = int.TryParse(Environment.GetEnvironmentVariable("REDIS_STREAM_TTL_MINUTES"), out int ttl) ? ttl : 10;

// =============================================================================
// AGENT SYSTEM PROMPTS
// =============================================================================

const string ConversationalAgentSystemPrompt = """
    You are a friendly and helpful travel planning assistant. Your job is to have a 
    conversation with users to understand their travel needs and help plan the perfect trip.

    ## Conversation Goals
    1. Greet the user warmly and ask how you can help with their travel plans
    2. Collect the following information through natural conversation:
       - Their name
       - Travel preferences (beach, adventure, cultural, relaxation, etc.)
       - Trip duration (number of days)
       - Budget (amount and currency)
       - Travel dates (specific dates or time range)
       - Special requirements (dietary, accessibility, traveling with children, etc.)
    
    ## Available Tools
    
    | Tool | Purpose |
    |------|---------|
    | **PlanTrip** | Start planning after collecting all required info |
    | **MonitorTripPlanning** | Check planning progress (call in loop until complete) |
    | **GetTripPlanDetails** | Retrieve the full trip plan when ready |
    | **RespondToTravelPlan** | Submit user's approval or rejection decision |

    ## Trip Planning Workflow
    
    **STEP 1 - Start Planning:**
    When user provides all trip details, call PlanTrip and tell them:
    "I've started planning your trip! It takes about 30 seconds to find destinations, 
    create your itinerary, and gather recommendations."
    
    **STEP 2 - Monitor Progress:**
    When user asks about progress, call MonitorTripPlanning with the orchestrationId.
    - If IsWaitingForApproval is true → Call GetTripPlanDetails and present the plan
    - If still in progress → Share the StatusMessage with the user
    
    **STEP 3 - Handle Decision:**
    When user approves or rejects, call RespondToTravelPlan with their decision.
    
    ## Required Information Checklist
    - ✅ User's name
    - ✅ Travel preferences (type of trip)
    - ✅ Duration (number of days)
    - ✅ Budget (with currency)
    - ✅ Travel dates
    - ⭕ Special requirements (optional)
    
    ## Guidelines
    - Be conversational - don't ask for all information at once
    - Always include EstimatedTotalCost and exchange rate in AdditionalNotes
    - Share status updates naturally as planning progresses
    """;

const string DestinationRecommenderPrompt = """
    You are a travel destination expert who recommends destinations based on user preferences.
    
    Based on the user's preferences, budget, duration, travel dates, and special requirements, 
    recommend 3 travel destinations with detailed explanations for each recommendation.
    
    Return your response as JSON:
    {
        "Recommendations": [
            {
                "DestinationName": "string",
                "Description": "string",
                "Reasoning": "string",
                "MatchScore": number (0-100)
            }
        ]
    }
    """;

const string ItineraryPlannerPrompt = """
    You are a travel itinerary planner. Create concise day-by-day travel plans.
    
    ## Response Guidelines
    - Keep descriptions under 50 characters
    - Include 2-4 activities per day maximum
    - Use abbreviated time formats (9AM not 9:00 AM)
    
    ## Currency Conversion Requirements
    You have access to a currency converter tool. Follow these steps:
    
    1. **Identify currencies**: User's budget currency and destination's local currency
    2. **Get exchange rate**: Call GetExchangeRate(fromCurrency, toCurrency)
    3. **Format costs**: Show local currency first, then user's currency
       Example: '250,000 IDR (15 USD)' or '50 EUR (55 USD)'
    4. **Include in notes**: 'Exchange rate: 1 USD = 16,500 IDR'
    
    ## Cost Calculation (CRITICAL)
    - List all activity costs as you create them
    - Sum only the actual costs (ignore Free/Varies)
    - EstimatedTotalCost = sum of activity costs only
    - DO NOT use budget amount or guess round numbers
    
    Return your response as JSON:
    {
        "DestinationName": "string",
        "TravelDates": "string",
        "DailyPlan": [
            {
                "Day": number,
                "Date": "string",
                "Activities": [
                    {
                        "Time": "string",
                        "ActivityName": "string",
                        "Description": "string",
                        "Location": "string",
                        "EstimatedCost": "string"
                    }
                ]
            }
        ],
        "EstimatedTotalCost": "string",
        "AdditionalNotes": "string"
    }
    """;

const string LocalRecommendationsPrompt = """
    You are a local expert who provides recommendations for restaurants and attractions.
    Provide specific recommendations with practical details like operating hours, pricing, and tips.
    
    Return your response as JSON:
    {
        "Attractions": [
            {
                "Name": "string",
                "Category": "string",
                "Description": "string",
                "Location": "string",
                "VisitDuration": "string",
                "EstimatedCost": "string",
                "Rating": number
            }
        ],
        "Restaurants": [
            {
                "Name": "string",
                "Cuisine": "string",
                "Description": "string",
                "Location": "string",
                "PriceRange": "string",
                "Rating": number
            }
        ],
        "InsiderTips": "string"
    }
    """;

// =============================================================================
// AZURE FUNCTIONS APPLICATION SETUP
// =============================================================================

FunctionsApplicationBuilder builder = FunctionsApplication
    .CreateBuilder(args)
    .ConfigureFunctionsWebApplication()
    .ConfigureDurableAgents(agents =>
    {
        // ---------------------------------------------------------------------
        // Agent 1: Conversational Travel Agent (Primary User Interface)
        // ---------------------------------------------------------------------
        // This agent handles all user interactions, collects travel preferences,
        // and orchestrates the trip planning workflow using tool calls.
        // ---------------------------------------------------------------------
        agents.AddAIAgentFactory("ConversationalTravelAgent", sp =>
        {
            var chatClient = new AzureOpenAIClient(new Uri(openAiEndpoint), new DefaultAzureCredential())
                .GetChatClient(openAiDeployment)
                .AsIChatClient();

            var planTripTools = new PlanTripTool(sp.GetRequiredService<ILogger<PlanTripTool>>());

            return chatClient.CreateAIAgent(
                instructions: ConversationalAgentSystemPrompt,
                name: "ConversationalTravelAgent",
                services: sp,
                tools:
                [
                    AIFunctionFactory.Create(planTripTools.PlanTrip),
                    AIFunctionFactory.Create(planTripTools.MonitorTripPlanning),
                    AIFunctionFactory.Create(planTripTools.GetTripPlanDetails),
                    AIFunctionFactory.Create(planTripTools.RespondToTravelPlan)
                ]);
        });

        // ---------------------------------------------------------------------
        // Agent 2: Destination Recommender
        // ---------------------------------------------------------------------
        // Analyzes user preferences and suggests 3 matching destinations.
        // ---------------------------------------------------------------------
        agents.AddAIAgentFactory("DestinationRecommenderAgent", sp =>
        {
            var chatClient = new AzureOpenAIClient(new Uri(openAiEndpoint), new DefaultAzureCredential())
                .GetChatClient(openAiDeployment)
                .AsIChatClient();

            return chatClient.CreateAIAgent(
                instructions: DestinationRecommenderPrompt,
                name: "DestinationRecommenderAgent",
                services: sp);
        });

        // ---------------------------------------------------------------------
        // Agent 3: Itinerary Planner (with Currency Converter Tool)
        // ---------------------------------------------------------------------
        // Creates day-by-day travel itineraries with cost estimates.
        // Has access to real-time currency conversion.
        // ---------------------------------------------------------------------
        agents.AddAIAgentFactory("ItineraryPlannerAgent", sp =>
        {
            var chatClient = new AzureOpenAIClient(new Uri(openAiEndpoint), new DefaultAzureCredential())
                .GetChatClient(openAiDeployment)
                .AsIChatClient();

            return chatClient.CreateAIAgent(
                instructions: ItineraryPlannerPrompt,
                name: "ItineraryPlannerAgent",
                services: sp,
                tools:
                [
                    AIFunctionFactory.Create(CurrencyConverterTool.ConvertCurrency),
                    AIFunctionFactory.Create(CurrencyConverterTool.GetExchangeRate)
                ]);
        });

        // ---------------------------------------------------------------------
        // Agent 4: Local Recommendations
        // ---------------------------------------------------------------------
        // Provides restaurant and attraction recommendations for destinations.
        // ---------------------------------------------------------------------
        agents.AddAIAgentFactory("LocalRecommendationsAgent", sp =>
        {
            var chatClient = new AzureOpenAIClient(new Uri(openAiEndpoint), new DefaultAzureCredential())
                .GetChatClient(openAiDeployment)
                .AsIChatClient();

            return chatClient.CreateAIAgent(
                instructions: LocalRecommendationsPrompt,
                name: "LocalRecommendationsAgent",
                services: sp);
        });
    });

// =============================================================================
// APPLICATION SERVICES
// =============================================================================

// -----------------------------------------------------------------------------
// Telemetry: Application Insights
// -----------------------------------------------------------------------------
builder.Services.AddApplicationInsightsTelemetryWorkerService();
builder.Services.ConfigureFunctionsApplicationInsights();

builder.Logging.Services.Configure<LoggerFilterOptions>(options =>
{
    var defaultRule = options.Rules.FirstOrDefault(
        rule => rule.ProviderName == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");
    if (defaultRule is not null)
    {
        options.Rules.Remove(defaultRule);
    }
});

// -----------------------------------------------------------------------------
// HTTP Client: Currency Conversion API
// -----------------------------------------------------------------------------
builder.Services.AddHttpClient("CurrencyConverter", client =>
{
    client.BaseAddress = new Uri("https://open.er-api.com/v6/");
    client.Timeout = TimeSpan.FromSeconds(30);
});

// -----------------------------------------------------------------------------
// Redis: Reliable Response Streaming
// -----------------------------------------------------------------------------
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("RedisConnection");

    if (useRedisManagedIdentity && !string.IsNullOrEmpty(redisHostName))
    {
        // Production: Use Microsoft Entra ID (managed identity) authentication
        int sslPort = int.TryParse(redisSslPort, out int port) ? port : 6380;
        logger.LogInformation("Connecting to Redis at {HostName}:{Port} with managed identity", redisHostName, sslPort);

        var configOptions = ConfigurationOptions.Parse($"{redisHostName}:{sslPort}");
        configOptions.Ssl = true;
        configOptions.AbortOnConnectFail = false;
        configOptions.ConnectTimeout = 60000;
        configOptions.AsyncTimeout = 30000;
        configOptions.SyncTimeout = 30000;

        var credential = new DefaultAzureCredential();
        var connectionTask = Task.Run(async () =>
        {
            await configOptions.ConfigureForAzureWithTokenCredentialAsync(credential);
            return await ConnectionMultiplexer.ConnectAsync(configOptions);
        });

        try
        {
            var connection = connectionTask.GetAwaiter().GetResult();
            logger.LogInformation("Successfully connected to Azure Cache for Redis with managed identity");
            return connection;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to connect to Redis with managed identity");
            throw;
        }
    }
    else
    {
        // Development: Use connection string (local Redis or Azurite)
        var connectionStr = redisConnectionString ?? "localhost:6379";
        logger.LogInformation("Connecting to Redis at {ConnectionString}", connectionStr);
        return ConnectionMultiplexer.Connect(connectionStr);
    }
});

// Response handler: Captures agent outputs and publishes to Redis Streams
builder.Services.AddSingleton(sp => new RedisStreamResponseHandler(
    sp.GetRequiredService<IConnectionMultiplexer>(),
    TimeSpan.FromMinutes(redisStreamTtlMinutes)));

builder.Services.AddSingleton<IAgentResponseHandler>(sp =>
    sp.GetRequiredService<RedisStreamResponseHandler>());

// -----------------------------------------------------------------------------
// Azure Blob Storage: Travel Plan Persistence
// -----------------------------------------------------------------------------
builder.Services.AddAzureClients(clientBuilder =>
{
    clientBuilder.UseCredential(new DefaultAzureCredential());

    var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
    if (!string.IsNullOrEmpty(connectionString))
    {
        // Development: Use connection string (Azurite emulator)
        clientBuilder.AddBlobServiceClient(connectionString);
    }
    else
    {
        // Production: Use Managed Identity with storage account name
        var storageAccountName = Environment.GetEnvironmentVariable("AzureWebJobsStorage__accountName")
            ?? throw new InvalidOperationException("AzureWebJobsStorage__accountName is required for production.");

        clientBuilder.AddBlobServiceClient(
            new Uri($"https://{storageAccountName}.blob.core.windows.net"),
            new DefaultAzureCredential());
    }
});

// -----------------------------------------------------------------------------
// CORS: Cross-Origin Resource Sharing
// -----------------------------------------------------------------------------
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var allowedOrigins = Environment.GetEnvironmentVariable("ALLOWED_ORIGINS") ?? "*";
        var origins = allowedOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries);

        if (origins.Length == 1 && origins[0] == "*")
        {
            // Development: Allow any origin
            policy.AllowAnyOrigin()
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .WithExposedHeaders("x-conversation-id");
        }
        else
        {
            // Production: Restrict to specific origins
            policy.WithOrigins(origins)
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials()
                  .WithExposedHeaders("x-conversation-id");
        }
    });
});

// =============================================================================
// START APPLICATION
// =============================================================================

var app = builder.Build();

// Initialize the currency converter tool with HTTP client factory
var httpClientFactory = app.Services.GetRequiredService<IHttpClientFactory>();
CurrencyConverterTool.Initialize(httpClientFactory);

app.Run();