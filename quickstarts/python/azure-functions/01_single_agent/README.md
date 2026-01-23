# 01 - Single Agent

This sample demonstrates how to host a single Azure OpenAI-powered agent inside Azure Functions using the Agent Framework with Durable Task Scheduler (DTS) as the durable backend.

## Overview

The sample creates a simple "Joker" agent that responds to requests with jokes. It demonstrates:

- Using `AzureOpenAIChatClient` to connect to Azure OpenAI
- Using `AgentFunctionApp` to expose HTTP endpoints via Durable Functions
- Configuring Durable Task Scheduler as the durable backend

## Prerequisites

### Local Development
1. [Azure Functions Core Tools 4.x](https://learn.microsoft.com/azure/azure-functions/functions-run-local)
2. [Azurite storage emulator](https://learn.microsoft.com/azure/storage/common/storage-install-azurite)
3. [Durable Task Scheduler emulator](https://github.com/Azure-Samples/Durable-Task-Scheduler) (optional for local dev)
4. An [Azure OpenAI](https://azure.microsoft.com/products/ai-services/openai-service) resource with a chat deployment
5. Python 3.11+

### Azure Deployment
1. [Azure Developer CLI (azd)](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd)
2. An Azure subscription

## Local Setup

1. Create and activate a virtual environment:
   ```bash
   python -m venv .venv
   source .venv/bin/activate  # Linux/macOS
   # or
   .venv\Scripts\Activate.ps1  # Windows PowerShell
   ```

2. Install dependencies:
   ```bash
   pip install -r requirements.txt
   ```

3. Copy the settings template:
   ```bash
   cp local.settings.json.template local.settings.json
   ```

4. Update `local.settings.json` with your Azure OpenAI details:
   - `AZURE_OPENAI_ENDPOINT`: Your Azure OpenAI endpoint
   - `AZURE_OPENAI_CHAT_DEPLOYMENT_NAME`: Your chat model deployment name

5. Authenticate with Azure CLI:
   ```bash
   az login
   ```

6. Start Azurite:
   ```bash
   azurite
   ```

7. Start the Functions host:
   ```bash
   func start
   ```

## Azure Deployment

Deploy to Azure using the Azure Developer CLI:

```bash
azd auth login
azd up
```

This will:
- Provision all required Azure resources (Function App, Storage, DTS, Azure OpenAI)
- Deploy the function code
- Configure managed identity for secure access

## Usage

### Health Check
```http
GET /api/health
```

### Send a Message to the Agent
```http
POST /api/agents/Joker/run
Content-Type: application/json

{
  "message": "Tell me a joke about cloud computing.",
  "thread_id": "my-thread-001"
}
```

Or with plain text:
```http
POST /api/agents/Joker/run

Tell me a programming joke.
```

### Response
```json
{
  "status": "accepted",
  "response": "Agent request accepted",
  "conversation_id": "<guid>",
  "correlation_id": "<guid>"
}
```

## Project Structure

```
01_single_agent/
├── function_app.py              # Main function app code
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
┌─────────────┐     ┌─────────────────┐     ┌──────────────────┐
│   Client    │────▶│  Azure Function │────▶│   Azure OpenAI   │
└─────────────┘     │   (Agent Host)  │     └──────────────────┘
                    └────────┬────────┘
                             │
                             ▼
                    ┌─────────────────┐
                    │  Durable Task   │
                    │   Scheduler     │
                    └─────────────────┘
```
