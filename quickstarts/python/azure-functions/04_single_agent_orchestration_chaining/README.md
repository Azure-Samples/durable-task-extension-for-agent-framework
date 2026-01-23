# 04 - Single Agent Orchestration Chaining

This sample demonstrates how to chain two runs of a single agent inside a Durable Functions orchestration, maintaining conversation context across invocations.

## Overview

The sample creates a WriterAgent that refines text through sequential invocations. It demonstrates:

- Using Durable Functions orchestration with Agent Framework
- Chaining multiple agent invocations on the same conversation thread
- Sequential processing where output of one call feeds into the next
- Custom HTTP endpoints for orchestration control

## How It Works

1. The orchestration starts and calls the WriterAgent to generate an initial inspirational sentence
2. The same agent is called again with the previous output, asking it to refine the text further
3. Both calls share the same conversation thread, maintaining context
4. The final refined output is returned as the orchestration result

## Prerequisites

### Local Development
1. [Azure Functions Core Tools 4.x](https://learn.microsoft.com/azure/azure-functions/functions-run-local)
2. [Azurite storage emulator](https://learn.microsoft.com/azure/storage/common/storage-install-azurite)
3. An [Azure OpenAI](https://azure.microsoft.com/products/ai-services/openai-service) resource
4. Python 3.11+

### Azure Deployment
1. [Azure Developer CLI (azd)](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd)
2. An Azure subscription

## Local Setup

1. Create and activate a virtual environment:
   ```bash
   python -m venv .venv
   source .venv/bin/activate
   ```

2. Install dependencies:
   ```bash
   pip install -r requirements.txt
   ```

3. Copy and configure settings:
   ```bash
   cp local.settings.json.template local.settings.json
   ```

4. Start Azurite and the function:
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

### Start the Orchestration
```http
POST /api/singleagent/run
```

Response:
```json
{
  "message": "Single-agent orchestration started.",
  "instanceId": "<guid>",
  "statusQueryGetUri": "http://localhost:7071/api/singleagent/status/<guid>"
}
```

### Check Orchestration Status
```http
GET /api/singleagent/status/{instanceId}
```

Response (when completed):
```json
{
  "instanceId": "<guid>",
  "runtimeStatus": "Completed",
  "output": "Learning is a journey where curiosity turns effort into mastery."
}
```

## Project Structure

```
04_single_agent_orchestration_chaining/
├── function_app.py              # Orchestration and agent code
├── requirements.txt             # Python dependencies
├── host.json                    # Azure Functions config (DTS enabled)
├── local.settings.json.template # Local settings template
├── azure.yaml                   # Azure Developer CLI config
├── demo.http                    # HTTP test requests
├── README.md                    # This file
└── infra/                       # Bicep infrastructure
```

## Architecture

```
┌─────────────┐     ┌─────────────────────────────────────────┐
│   Client    │────▶│            Orchestration                │
└─────────────┘     │  ┌─────────────┐    ┌─────────────┐    │
                    │  │  Step 1:    │───▶│  Step 2:    │    │
                    │  │  Generate   │    │  Refine     │    │
                    │  │  sentence   │    │  further    │    │
                    │  └──────┬──────┘    └──────┬──────┘    │
                    │         │                   │           │
                    │         └───────────────────┘           │
                    │            Same Thread (context)        │
                    └─────────────────┬───────────────────────┘
                                      │
                                      ▼
                             ┌─────────────────┐
                             │  Durable Task   │
                             │   Scheduler     │
                             └─────────────────┘
```

## Key Concepts

### Orchestration Chaining
The orchestration pattern chains multiple agent calls sequentially, where each call can use the output from the previous call.

### Shared Thread
Both agent invocations use the same `writer_thread`, allowing the agent to maintain context and conversation history across calls.

### Durable Execution
The orchestration is durable - if the function restarts, it will resume from where it left off without re-executing completed steps.
