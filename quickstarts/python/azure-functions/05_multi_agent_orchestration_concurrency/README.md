# 05 - Multi-Agent Orchestration Concurrency

This sample demonstrates how to fan out concurrent agent runs using Durable Functions orchestration, executing multiple agents in parallel and aggregating their results.

## Overview

The sample creates two domain-specific agents (PhysicistAgent and ChemistAgent) that answer questions from their respective perspectives. It demonstrates:

- Running multiple agents concurrently using `task_all`
- Fan-out/fan-in pattern with Durable Functions
- Aggregating results from multiple parallel agent runs
- Domain-specific AI agents with different expertise

## How It Works

1. User submits a question (e.g., "What is temperature?")
2. The orchestration fans out to both agents in parallel
3. Each agent answers from their domain perspective
4. Results are aggregated and returned together

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
POST /api/multiagent/run
Content-Type: text/plain

What is temperature?
```

Response:
```json
{
  "message": "Multi-agent concurrent orchestration started.",
  "prompt": "What is temperature?",
  "instanceId": "<guid>",
  "statusQueryGetUri": "http://localhost:7071/api/multiagent/status/<guid>"
}
```

### Check Orchestration Status
```http
GET /api/multiagent/status/{instanceId}
```

Response (when completed):
```json
{
  "instanceId": "<guid>",
  "runtimeStatus": "Completed",
  "output": {
    "physicist": "Temperature measures the average kinetic energy of particles in a system.",
    "chemist": "Temperature reflects how molecular motion influences reaction rates and equilibria."
  }
}
```

## Project Structure

```
05_multi_agent_orchestration_concurrency/
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
                              ┌─────────────────────────────────┐
                              │         Orchestration           │
┌─────────────┐               │  ┌───────────────────────────┐  │
│   Client    │──────────────▶│  │      Fan-out (task_all)   │  │
│             │               │  └─────────────┬─────────────┘  │
│  "What is   │               │                │                │
│  temperature?"              │     ┌──────────┴──────────┐     │
└─────────────┘               │     ▼                     ▼     │
       ▲                      │ ┌─────────────┐   ┌─────────────┐│
       │                      │ │PhysicistAgent│   │ChemistAgent ││
       │                      │ │(physics POV)│   │(chemistry POV)│
       │                      │ └──────┬──────┘   └──────┬──────┘│
       │                      │        │                 │       │
       │                      │        └────────┬────────┘       │
       │                      │                 ▼                │
       │                      │  ┌─────────────────────────────┐ │
       └──────────────────────│──│      Fan-in (aggregate)     │ │
                              │  └─────────────────────────────┘ │
                              └─────────────────┬────────────────┘
                                                │
                                                ▼
                                       ┌─────────────────┐
                                       │  Durable Task   │
                                       │   Scheduler     │
                                       └─────────────────┘
```

## Key Concepts

### Fan-Out/Fan-In Pattern
The orchestration uses `context.task_all()` to execute multiple agent calls in parallel, then waits for all to complete before aggregating results.

### Domain-Specific Agents
Each agent has specialized instructions that guide its response perspective, providing diverse viewpoints on the same question.

### Parallel Execution
Both agents run simultaneously, reducing total execution time compared to sequential calls.
