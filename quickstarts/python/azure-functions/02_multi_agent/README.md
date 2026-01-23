# 02 - Multi-Agent

This sample demonstrates how to host multiple Azure OpenAI agents inside a single Azure Functions app using the Agent Framework with Durable Task Scheduler (DTS) as the durable backend.

## Overview

The sample creates two agents with different capabilities:
- **WeatherAgent**: Provides weather information using a custom tool
- **MathAgent**: Performs calculations like tip computations using a custom tool

It demonstrates:
- Creating multiple agents with the same `AzureOpenAIChatClient`
- Registering multiple agents with `AgentFunctionApp`
- Using custom tool functions that agents can invoke
- Configuring Durable Task Scheduler as the durable backend

## Prerequisites

### Local Development
1. [Azure Functions Core Tools 4.x](https://learn.microsoft.com/azure/azure-functions/functions-run-local)
2. [Azurite storage emulator](https://learn.microsoft.com/azure/storage/common/storage-install-azurite)
3. An [Azure OpenAI](https://azure.microsoft.com/products/ai-services/openai-service) resource with a chat deployment
4. Python 3.11+

### Azure Deployment
1. [Azure Developer CLI (azd)](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd)
2. An Azure subscription

## Local Setup

1. Create and activate a virtual environment:
   ```bash
   python -m venv .venv
   source .venv/bin/activate  # Linux/macOS
   ```

2. Install dependencies:
   ```bash
   pip install -r requirements.txt
   ```

3. Copy the settings template:
   ```bash
   cp local.settings.json.template local.settings.json
   ```

4. Update `local.settings.json` with your Azure OpenAI details

5. Authenticate and start:
   ```bash
   az login
   azurite &
   func start
   ```

## Azure Deployment

```bash
azd auth login
azd up
```

## Usage

### Health Check
```http
GET /api/health
```

Returns information about all registered agents.

### Weather Agent
```http
POST /api/agents/WeatherAgent/run
Content-Type: application/json

{
  "message": "What is the weather in Seattle?",
  "thread_id": "weather-001"
}
```

### Math Agent
```http
POST /api/agents/MathAgent/run
Content-Type: application/json

{
  "message": "Calculate a 20% tip on a $50 bill",
  "thread_id": "math-001"
}
```

## Project Structure

```
02_multi_agent/
├── function_app.py              # Main function app with both agents
├── requirements.txt             # Python dependencies
├── host.json                    # Azure Functions host configuration (DTS enabled)
├── local.settings.json.template # Local settings template
├── azure.yaml                   # Azure Developer CLI configuration
├── demo.http                    # HTTP test requests
├── README.md                    # This file
└── infra/                       # Bicep infrastructure
    ├── main.bicep
    ├── main.parameters.json
    ├── abbreviations.json
    └── app/
        └── dts.bicep
```

## Architecture

```
                    ┌─────────────────────────────────────┐
                    │         Azure Functions             │
┌─────────────┐     │  ┌─────────────┐ ┌─────────────┐   │     ┌──────────────────┐
│   Client    │────▶│  │WeatherAgent │ │  MathAgent  │   │────▶│   Azure OpenAI   │
└─────────────┘     │  │ (get_weather)│ │(calc_tip)   │   │     └──────────────────┘
                    │  └─────────────┘ └─────────────┘   │
                    └────────────────┬───────────────────┘
                                     │
                                     ▼
                            ┌─────────────────┐
                            │  Durable Task   │
                            │   Scheduler     │
                            └─────────────────┘
```
