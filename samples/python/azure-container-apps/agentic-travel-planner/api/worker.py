"""
Travel Planner Worker - Durable Task Worker for AI Travel Planning

This worker runs the travel planning orchestration using the Microsoft Agent Framework
with DurableTask integration. It coordinates AI agents to create comprehensive travel plans.

Prerequisites:
- Set AZURE_OPENAI_ENDPOINT and AZURE_OPENAI_DEPLOYMENT_NAME
  (plus Azure CLI authentication via DefaultAzureCredential)
- Start a Durable Task Scheduler (e.g., using Docker)
"""

import asyncio
import os
import logging
import random
from collections.abc import Generator
from datetime import timedelta
from pathlib import Path
from typing import Any

from dotenv import load_dotenv
from azure.identity import DefaultAzureCredential
from durabletask.task import OrchestrationContext, ActivityContext, Task, when_any
from durabletask.azuremanaged.worker import DurableTaskSchedulerWorker
from agent_framework.azure import AzureOpenAIChatClient
from agent_framework_durabletask import DurableAIAgentWorker, DurableAIAgentOrchestrationContext

from models.travel_models import (
    TravelRequest,
    DestinationRecommendations,
    Itinerary,
    LocalRecommendations,
    BookingResult,
    TravelPlan,
    TravelPlanResult,
)
from tools.currency_converter import convert_currency, get_exchange_rate

# Load environment variables from project root
env_path = Path(__file__).parent.parent / '.env'
load_dotenv(dotenv_path=env_path)

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s'
)
logger = logging.getLogger(__name__)


def parse_agent_response(result: Any, model_class: type) -> Any:
    """Parse agent response to extract and validate the model.
    
    The new agent-framework SDK returns AgentResponse objects where text
    is accessible via .text property. This helper handles both old (.value)
    and new (.text) response formats.
    
    Args:
        result: The agent response object
        model_class: The Pydantic model class to parse into
        
    Returns:
        Parsed model instance or None if parsing fails
    """
    import json
    import re
    
    # Try the SDK's .value property first (may raise ValidationError)
    if hasattr(result, 'value'):
        try:
            parsed = result.value
            if isinstance(parsed, model_class):
                return parsed
        except Exception:
            pass
    
    # Get raw text from various possible attributes
    raw_text = None
    if hasattr(result, 'text'):
        raw_text = result.text
    else:
        raw_text = result
    
    logger.info(f"Parsing response, raw type: {type(raw_text)}")
    
    # If already the right type, return it
    if isinstance(raw_text, model_class):
        return raw_text
    
    # If it's a string, parse JSON
    if isinstance(raw_text, str):
        # Extract JSON from markdown code blocks if present
        json_match = re.search(r'```(?:json)?\s*([\s\S]*?)\s*```', raw_text)
        if json_match:
            raw_text = json_match.group(1)
        try:
            raw_text = json.loads(raw_text)
        except json.JSONDecodeError as e:
            logger.error(f"JSON parse error: {e}")
            return None
    
    # If it's a dict, construct the model
    if isinstance(raw_text, dict):
        try:
            return model_class(**raw_text)
        except Exception as e:
            logger.error(f"Model validation error: {e}")
            return None
    
    return raw_text

# ================== Configuration ==================

AZURE_OPENAI_ENDPOINT = os.getenv("AZURE_OPENAI_ENDPOINT")
AZURE_OPENAI_DEPLOYMENT_NAME = os.getenv("AZURE_OPENAI_DEPLOYMENT_NAME", "gpt-4.1")
TASKHUB_NAME = os.getenv("TASKHUB_NAME", "default")

# Parse DTS connection string or use legacy DURABLE_TASK_HOST
DTS_CONNECTION_STRING = os.getenv("DURABLE_TASK_SCHEDULER_CONNECTION_STRING", "")
DURABLE_TASK_HOST = os.getenv("DURABLE_TASK_HOST", "localhost:8080")

def parse_dts_connection_string(conn_str: str) -> tuple[str, str | None]:
    """Parse DTS connection string to extract endpoint and client ID.
    
    Format: Endpoint=https://...;Authentication=ManagedIdentity;ClientID=...
    
    Returns:
        Tuple of (endpoint, client_id) - client_id may be None
    """
    if not conn_str:
        return DURABLE_TASK_HOST, None
    
    parts = dict(part.split("=", 1) for part in conn_str.split(";") if "=" in part)
    endpoint = parts.get("Endpoint", DURABLE_TASK_HOST)
    client_id = parts.get("ClientID")
    return endpoint, client_id

DTS_ENDPOINT, DTS_CLIENT_ID = parse_dts_connection_string(DTS_CONNECTION_STRING)

if not AZURE_OPENAI_ENDPOINT:
    raise ValueError(
        "AZURE_OPENAI_ENDPOINT environment variable is not set. "
        "Please configure your Azure OpenAI endpoint in .env file."
    )

logger.info(f"Azure OpenAI Endpoint: {AZURE_OPENAI_ENDPOINT}")
logger.info(f"Azure OpenAI Deployment: {AZURE_OPENAI_DEPLOYMENT_NAME}")
logger.info(f"Durable Task Endpoint: {DTS_ENDPOINT}")
logger.info(f"Durable Task Client ID: {DTS_CLIENT_ID}")
logger.info(f"TaskHub Name: {TASKHUB_NAME}")

# ================== Create Azure OpenAI Chat Client ==================

chat_client = AzureOpenAIChatClient(
    endpoint=AZURE_OPENAI_ENDPOINT,
    deployment_name=AZURE_OPENAI_DEPLOYMENT_NAME,
    credential=DefaultAzureCredential()
)

# ================== Agent Definitions ==================

# Destination Recommender Agent
destination_recommender_agent = chat_client.as_agent(
    name="DestinationRecommenderAgent",
    instructions="""You are a travel destination expert who recommends destinations based on user preferences.
Based on the user's preferences, budget, duration, travel dates, and special requirements, recommend 3 travel destinations.
Provide a detailed explanation for each recommendation highlighting why it matches the user's preferences.

Return your response as a JSON object with this structure (use PascalCase for property names):
{
    "Recommendations": [
        {
            "DestinationName": "string",
            "Description": "string",
            "Reasoning": "string",
            "MatchScore": number (0-100)
        }
    ]
}"""
)

# Itinerary Planner Agent
itinerary_planner_agent = chat_client.as_agent(
    name="ItineraryPlannerAgent",
    instructions="""You are a travel itinerary planner. Create concise day-by-day travel plans with key activities and timing.

IMPORTANT: Keep responses compact:
- Descriptions MUST be under 50 characters each
- Include 2-4 activities per day maximum
- Use abbreviated formats for times (9AM not 9:00 AM)
- Keep location names short

CRITICAL - MINIMIZE TOOL CALLS:
You have access to currency tools but MUST minimize their use to avoid excessive API calls.

CURRENCY HANDLING - FOLLOW THESE RULES EXACTLY:

1. First, identify the user's budget currency (from the budget string, e.g., "$3000" = USD)
2. Identify the destination country's local currency (e.g., Japan=JPY, UK=GBP, Spain=EUR)

3. IF SAME CURRENCY (e.g., user has USD budget and destination uses USD):
   - DO NOT call any currency tools - this is critical!
   - Show all costs in USD only (e.g., "25 USD")
   - No conversion needed

4. IF DIFFERENT CURRENCIES (e.g., user has USD budget but destination uses EUR):
   - Call get_exchange_rate EXACTLY ONCE at the very start to get the rate
   - Store the rate and use simple multiplication for ALL conversions
   - NEVER call convert_currency for individual activities - just multiply!
   - Show costs as: local currency first, then user currency in parentheses
   - Example: "25 EUR (27 USD)" where 27 = 25 * exchange_rate

COST CALCULATION:
- Add up all numeric activity costs (ignore "Free" and "Varies")
- EstimatedTotalCost = sum of activity costs
- If currencies differ, show both: "162 EUR (177 USD)"

Return your response as a JSON object with this structure:
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
}""",
    tools=[get_exchange_rate, convert_currency]
)

# Local Recommendations Agent
local_recommendations_agent = chat_client.as_agent(
    name="LocalRecommendationsAgent",
    instructions="""You are a local expert who provides recommendations for restaurants and attractions.
Provide specific recommendations with practical details like operating hours, pricing, and tips.

Return your response as a JSON object with this structure:
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
}"""
)


# ================== Travel Planner Orchestration ==================

def travel_planner_orchestration(
    ctx: OrchestrationContext,
    input_data: dict
) -> Generator[Task[Any], Any, dict]:
    """Travel planner orchestration with multi-agent coordination and approval workflow.
    
    This orchestration:
    1. Gets destination recommendations from the Destination Recommender Agent
    2. Creates an itinerary using the Itinerary Planner Agent
    3. Gets local recommendations from the Local Recommendations Agent
    4. Waits for human approval with timeout
    5. Books the trip if approved
    
    Args:
        ctx: The orchestration context
        input_data: The travel request input data
        
    Yields:
        Task[Any]: Tasks that resolve to agent responses
        
    Returns:
        dict: The final travel plan result
        
    Raises:
        Exception: If any step fails
    """
    logger.debug("[Orchestration] Starting travel planner orchestration")
    
    # Create agent orchestration context - agents are registered with the worker
    agent_ctx = DurableAIAgentOrchestrationContext(ctx)
    
    # Parse travel request
    travel_request = TravelRequest(**input_data) if isinstance(input_data, dict) else input_data
    
    try:
        # Set initial status
        ctx.set_custom_status({
            "step": "GettingDestinations",
            "message": "Finding perfect destinations for you..."
        })
        
        # Step 1: Get destination recommendations
        logger.info("Step 1: Getting destination recommendations")
        destination_agent = agent_ctx.get_agent("DestinationRecommenderAgent")
        destination_session = destination_agent.create_session()
        
        destination_prompt = f"""Based on the following preferences, recommend 3 travel destinations:
User: {travel_request.user_name}
Preferences: {travel_request.preferences}
Duration: {travel_request.duration_in_days} days
Budget: {travel_request.budget}
Travel Dates: {travel_request.travel_dates}
Special Requirements: {travel_request.special_requirements}

Provide detailed explanations for each recommendation highlighting why it matches the user's preferences."""

        destinations_result = yield destination_agent.run(
            messages=destination_prompt,
            session=destination_session
        )
        
        # Parse the agent response using helper
        destinations = parse_agent_response(destinations_result, DestinationRecommendations)
        
        if not destinations or not destinations.recommendations:
            logger.error(f"No destinations found. Raw result: {destinations_result}")
            return {"error": "No destinations found"}
        
        # Get top destination
        top_destination = destinations.recommendations[0]
        logger.info(f"Top destination: {top_destination.destination_name}")
        
        # Update status
        ctx.set_custom_status({
            "step": "CreatingItinerary",
            "message": f"Creating itinerary for {top_destination.destination_name}...",
            "destination": top_destination.destination_name
        })
        
        # Step 2: Create itinerary for top destination
        logger.info("Step 2: Creating itinerary")
        itinerary_agent = agent_ctx.get_agent("ItineraryPlannerAgent")
        itinerary_session = itinerary_agent.create_session()
        
        itinerary_prompt = f"""Create a detailed daily itinerary for a trip to {top_destination.destination_name}:
Duration: {travel_request.duration_in_days} days
Budget: {travel_request.budget}
Travel Dates: {travel_request.travel_dates}
Special Requirements: {travel_request.special_requirements}

Include a mix of sightseeing, cultural activities, and relaxation time with realistic costs."""

        itinerary_result = yield itinerary_agent.run(
            messages=itinerary_prompt,
            session=itinerary_session
        )
        
        # Parse the agent response using helper
        itinerary = parse_agent_response(itinerary_result, Itinerary)
        
        # Update status
        ctx.set_custom_status({
            "step": "GettingLocalRecommendations",
            "message": f"Getting local tips for {top_destination.destination_name}...",
            "destination": top_destination.destination_name
        })
        
        # Step 3: Get local recommendations
        logger.info("Step 3: Getting local recommendations")
        local_agent = agent_ctx.get_agent("LocalRecommendationsAgent")
        local_session = local_agent.create_session()
        
        local_prompt = f"""Provide local recommendations for {top_destination.destination_name}:
Duration of Stay: {travel_request.duration_in_days} days
Include: Hidden gems, family-friendly options, authentic local experiences

Provide authentic local attractions, restaurants, and insider tips."""

        local_result = yield local_agent.run(
            messages=local_prompt,
            session=local_session
        )
        
        # Parse the agent response using helper
        local_recs = parse_agent_response(local_result, LocalRecommendations)
        
        logger.info("Local recommendations received")
        
        # Update status to waiting for approval
        ctx.set_custom_status({
            "step": "WaitingForApproval",
            "message": "Your travel plan is ready! Please review and approve.",
            "destination": top_destination.destination_name,
            "travelPlan": {
                "dates": itinerary.travel_dates if itinerary else "TBD",
                "cost": itinerary.estimated_total_cost if itinerary else "TBD",
                "dailyPlan": [day.model_dump(by_alias=True) for day in itinerary.daily_plan] if itinerary else [],
                "attractions": [a.model_dump(by_alias=True) for a in local_recs.attractions] if local_recs else [],
                "restaurants": [r.model_dump(by_alias=True) for r in local_recs.restaurants] if local_recs else [],
                "insiderTips": local_recs.insider_tips if local_recs else ""
            }
        })
        
        logger.info("Waiting for approval event...")
        
        # Step 4: Wait for approval event with timeout
        approval_task = ctx.wait_for_external_event("ApprovalEvent")
        timeout_task = ctx.create_timer(ctx.current_utc_datetime + timedelta(hours=24))
        
        logger.info("Created approval task and timeout task, yielding when_any...")
        
        winner = yield when_any([approval_task, timeout_task])
        
        logger.info(f"when_any returned, winner is approval_task: {winner == approval_task}")
        
        if winner == approval_task:
            # Timer is not explicitly cancelled - it will just expire harmlessly
            approval_result = approval_task.get_result()
            
            logger.info(f"Approval result received: {approval_result}")
            
            # Handle approval result
            if isinstance(approval_result, str):
                import json
                try:
                    approval_result = json.loads(approval_result)
                except:
                    approval_result = {"approved": False, "comments": "Invalid approval format"}
            
            if approval_result.get("approved", False):
                # Step 5: Book the trip
                ctx.set_custom_status({
                    "step": "BookingTrip",
                    "message": f"Booking your trip to {top_destination.destination_name}...",
                    "destination": top_destination.destination_name
                })
                
                booking_request = {
                    "destination_name": top_destination.destination_name,
                    "estimated_cost": itinerary.estimated_total_cost if itinerary else "TBD",
                    "travel_dates": itinerary.travel_dates if itinerary else "TBD",
                    "user_name": travel_request.user_name,
                    "approval_comments": approval_result.get("comments", "")
                }
                
                booking_result = yield ctx.call_activity(book_trip, input=booking_request)
                
                ctx.set_custom_status({
                    "step": "Completed",
                    "message": "Your trip has been booked!",
                    "destination": top_destination.destination_name,
                    "booking_id": booking_result.get("booking_id", "N/A")
                })
                
                # Build final result
                result = TravelPlanResult(
                    plan=TravelPlan(
                        destination_recommendations=destinations,
                        itinerary=itinerary,
                        local_recommendations=local_recs,
                        attractions=local_recs.attractions if local_recs else [],
                        restaurants=local_recs.restaurants if local_recs else [],
                        insider_tips=local_recs.insider_tips if local_recs else ""
                    ),
                    booking_result=BookingResult(**booking_result),
                    booking_confirmation=f"Booking confirmed for your trip to {top_destination.destination_name}! Confirmation ID: {booking_result.get('booking_id', 'N/A')}",
                    document_url=f"https://example.com/booking/{ctx.instance_id}"
                )
                
                return result.model_dump(by_alias=True)
            else:
                # Not approved
                ctx.set_custom_status({
                    "step": "Rejected",
                    "message": "Travel plan was not approved.",
                    "destination": top_destination.destination_name
                })
                
                result = TravelPlanResult(
                    plan=TravelPlan(
                        destination_recommendations=destinations,
                        itinerary=itinerary,
                        local_recommendations=local_recs
                    ),
                    booking_confirmation=f"Travel plan was not approved. Comments: {approval_result.get('comments', 'No comments provided')}"
                )
                return result.model_dump(by_alias=True)
        else:
            # Timeout - escalate for review
            logger.info("Timeout task won - travel plan timed out")
            result = TravelPlanResult(
                plan=TravelPlan(
                    destination_recommendations=destinations,
                    itinerary=itinerary,
                    local_recommendations=local_recs
                ),
                booking_confirmation="Travel plan timed out waiting for approval."
            )
            return result.model_dump(by_alias=True)
            
    except Exception as ex:
        import traceback
        logger.error(f"Orchestration error: {ex}")
        logger.error(f"Traceback: {traceback.format_exc()}")
        ctx.set_custom_status({
            "step": "Error",
            "message": str(ex)
        })
        return {"error": str(ex)}


# ================== Activity Functions ==================

def book_trip(ctx: ActivityContext, request: dict) -> dict:
    """Book the trip - simulates a booking process."""
    try:
        destination = request.get("destination_name", "Unknown")
        estimated_cost = request.get("estimated_cost", "TBD")
        
        # Generate booking confirmation
        booking_id = f"TRV-{random.randint(100000, 999999)}"
        
        logger.info(f"Booking trip to {destination} - Booking ID: {booking_id}")
        
        return {
            "booking_id": booking_id,
            "status": "confirmed",
            "destination": destination,
            "total_cost": estimated_cost,
            "confirmation_number": booking_id,
            "booking_date": "2025-08-07",
            "message": f"Trip to {destination} successfully booked!",
            "next_steps": "You will receive confirmation emails shortly with detailed itinerary and vouchers."
        }
    except Exception as ex:
        logger.error(f"Error in book_trip: {ex}")
        return {"status": "failed", "error": str(ex)}


# ================== Worker Setup ==================

def get_worker(
    taskhub: str | None = None,
    endpoint: str | None = None,
    log_handler: logging.Handler | None = None
) -> DurableTaskSchedulerWorker:
    """Create a configured DurableTaskSchedulerWorker.
    
    Args:
        taskhub: Task hub name (defaults to TASKHUB_NAME env var)
        endpoint: Scheduler endpoint (defaults to DTS_ENDPOINT from connection string)
        log_handler: Optional logging handler
        
    Returns:
        Configured DurableTaskSchedulerWorker instance
    """
    taskhub_name = taskhub or TASKHUB_NAME
    endpoint_url = endpoint or DTS_ENDPOINT
    
    logger.info(f"Creating worker with taskhub: {taskhub_name}")
    logger.info(f"Creating worker with endpoint: {endpoint_url}")
    
    # Use no credential for local emulator, otherwise use DefaultAzureCredential
    is_local = "localhost" in endpoint_url or "127.0.0.1" in endpoint_url
    credential = None if is_local else DefaultAzureCredential()
    
    return DurableTaskSchedulerWorker(
        host_address=endpoint_url,
        secure_channel=not is_local,
        taskhub=taskhub_name,
        token_credential=credential,
        log_handler=log_handler
    )


def setup_worker(worker: DurableTaskSchedulerWorker) -> DurableAIAgentWorker:
    """Set up the worker with agents, orchestrations, and activities registered.
    
    Args:
        worker: The DurableTaskSchedulerWorker instance
        
    Returns:
        DurableAIAgentWorker with agents, orchestrations, and activities registered
    """
    # Wrap it with the agent worker
    agent_worker = DurableAIAgentWorker(worker)
    
    # Create and register agents
    logger.debug("Creating and registering agents...")
    agent_worker.add_agent(destination_recommender_agent)
    agent_worker.add_agent(itinerary_planner_agent)
    agent_worker.add_agent(local_recommendations_agent)
    
    logger.debug(f"✓ Registered agents: {agent_worker.registered_agent_names}")
    
    # Register activity functions
    logger.debug("Registering activity functions...")
    worker.add_activity(book_trip)  # type: ignore[arg-type]
    logger.debug("✓ Registered activity: book_trip")
    
    # Register the orchestration function
    logger.debug("Registering orchestration function...")
    worker.add_orchestrator(travel_planner_orchestration)  # type: ignore[arg-type]
    logger.debug(f"✓ Registered orchestration: {travel_planner_orchestration.__name__}")
    
    return agent_worker


# Legacy function for backward compatibility with app.py
def create_worker() -> DurableTaskSchedulerWorker:
    """Create and configure the Durable Task worker (legacy compatibility).
    
    Returns:
        Configured DurableTaskSchedulerWorker instance with orchestration and activities
    """
    worker = get_worker()
    
    # Register the orchestration and activities (agents are registered separately)
    worker.add_orchestrator(travel_planner_orchestration)  # type: ignore[arg-type]
    worker.add_activity(book_trip)  # type: ignore[arg-type]
    
    logger.info("Worker configured with orchestration and activities")
    
    return worker


async def main():
    """Main entry point for the worker process."""
    logger.info("Starting Travel Planner Durable Task Worker...")
    
    # Create a worker using the helper function
    worker = get_worker()
    
    # Setup worker with agents, orchestrations, and activities
    agent_worker = setup_worker(worker)
    
    logger.info("Worker is ready and listening for requests...")
    logger.info("Press Ctrl+C to stop.")
    
    try:
        # Start the worker (this blocks until stopped)
        agent_worker.start()
        
        # Keep the worker running
        while True:
            await asyncio.sleep(1)
    except KeyboardInterrupt:
        logger.debug("Worker shutdown initiated")
    finally:
        logger.info("Stopping worker...")
        agent_worker.stop()
        logger.info("Worker stopped")


if __name__ == "__main__":
    asyncio.run(main())