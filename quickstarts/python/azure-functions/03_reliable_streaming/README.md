# 03 - Reliable Streaming

This sample demonstrates how to implement reliable streaming for durable agents using Redis Streams, enabling clients to disconnect and reconnect without losing messages.

## Overview

The sample creates a TravelPlanner agent that generates detailed travel itineraries with streaming responses persisted to Redis. It demonstrates:

- Using Redis Streams for reliable, resumable message delivery
- Custom callback handlers for streaming response persistence
- Cursor-based pagination for stream resumption
- Server-Sent Events (SSE) and plain text response formats

## Architecture

```
┌─────────────┐     ┌─────────────────┐     ┌──────────────────┐
│   Client    │────▶│  Azure Function │────▶│   Azure OpenAI   │
└──────┬──────┘     │   (Agent Host)  │     └──────────────────┘
       │            └────────┬────────┘
       │                     │ streaming
       │                     ▼ callback
       │            ┌─────────────────┐
       │            │  Redis Streams  │
       │            │  (persistence)  │
       │            └────────┬────────┘
       │                     │
       └─────────────────────┘
         resume via cursor
```

## Prerequisites

### Local Development
1. [Azure Functions Core Tools 4.x](https://learn.microsoft.com/azure/azure-functions/functions-run-local)
2. [Azurite storage emulator](https://learn.microsoft.com/azure/storage/common/storage-install-azurite)
3. [Redis](https://redis.io/) - run via Docker: `docker run -d --name redis -p 6379:6379 redis:latest`
4. An [Azure OpenAI](https://azure.microsoft.com/products/ai-services/openai-service) resource
5. Python 3.11+

### Azure Deployment
1. [Azure Developer CLI (azd)](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd)
2. An Azure subscription
3. Azure Cache for Redis (provisioned by infrastructure)

## Local Setup

1. Start Redis:
   ```bash
   docker run -d --name redis -p 6379:6379 redis:latest
   ```

2. Create and activate a virtual environment:
   ```bash
   python -m venv .venv
   source .venv/bin/activate
   ```

3. Install dependencies:
   ```bash
   pip install -r requirements.txt
   ```

4. Copy and configure settings:
   ```bash
   cp local.settings.json.template local.settings.json
   # Update Azure OpenAI settings
   ```

5. Start Azurite and the function:
   ```bash
   azurite &
   func start
   ```

## Azure Deployment

```bash
azd auth login
azd up
```

## Usage

### Workflow

1. **Start an agent run** - Submit a travel planning request
2. **Get conversation ID** - Extract from the response
3. **Stream responses** - Poll the stream endpoint with the conversation ID
4. **Resume if disconnected** - Use the cursor from the last received event

### Start Agent Run
```http
POST /api/agents/TravelPlanner/run
Content-Type: text/plain

Plan a 3-day trip to Tokyo
```

Response:
```json
{
  "status": "accepted",
  "conversation_id": "abc123-...",
  "correlation_id": "..."
}
```

### Stream Responses (SSE)
```http
GET /api/agent/stream/{conversation_id}
Accept: text/event-stream
```

Response (SSE format):
```
id: 1706123456789-0
event: message
data: Here's your detailed 3-day Tokyo itinerary...

id: 1706123456790-0
event: message
data: **Day 1: Arrival & Shibuya**...

id: 1706123456800-0
event: done
data: [DONE]
```

### Resume from Cursor
```http
GET /api/agent/stream/{conversation_id}?cursor=1706123456789-0
```

## Project Structure

```
03_reliable_streaming/
├── function_app.py                    # Main function app with streaming
├── redis_stream_response_handler.py   # Redis Streams handler
├── tools.py                           # Travel planning tools
├── requirements.txt                   # Python dependencies
├── host.json                          # Azure Functions config (DTS enabled)
├── local.settings.json.template       # Local settings template
├── azure.yaml                         # Azure Developer CLI config
├── demo.http                          # HTTP test requests
├── README.md                          # This file
└── infra/                             # Bicep infrastructure
```

## Key Concepts

### Redis Stream Callback
The `RedisStreamCallback` class implements `AgentResponseCallbackProtocol` to persist streaming chunks to Redis as they arrive.

### Cursor-Based Resumption
Each chunk has a unique Redis entry ID that serves as a cursor. Clients can reconnect and resume from any point using this cursor.

### Stream TTL
Streams are automatically expired after a configurable period (default 10 minutes) to prevent unbounded growth.
