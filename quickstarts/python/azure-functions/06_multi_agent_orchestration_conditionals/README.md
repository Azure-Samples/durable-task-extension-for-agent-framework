# 06 - Multi-Agent Orchestration Conditionals

This sample demonstrates conditional branching in orchestrations using multiple agents, where different paths are taken based on agent analysis results.

## Overview

The sample creates an email processing pipeline with two agents:
- **SpamDetectionAgent**: Analyzes emails for spam content
- **EmailAssistantAgent**: Drafts professional responses to legitimate emails

It demonstrates:
- Conditional branching based on agent analysis results
- Structured JSON responses with Pydantic validation
- Activity functions for side effects (marking spam, sending emails)
- Multi-agent orchestration with different execution paths

## How It Works

1. User submits an email for processing
2. SpamDetectionAgent analyzes if the email is spam
3. **If spam**: Mark as spam and return early
4. **If legitimate**: EmailAssistantAgent drafts a response, then sends it

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

### Process a Legitimate Email
```http
POST /api/spamdetection/run
Content-Type: application/json

{
  "email_id": "email-001",
  "email_content": "Hi John, Can you send me the Q4 report? Thanks!"
}
```

Response (when completed):
```json
{
  "instanceId": "<guid>",
  "runtimeStatus": "Completed",
  "output": "Email sent: Thank you for reaching out..."
}
```

### Process a Spam Email
```http
POST /api/spamdetection/run
Content-Type: application/json

{
  "email_id": "email-002",
  "email_content": "URGENT! You've won $1,000,000! Click here now!"
}
```

Response (when completed):
```json
{
  "instanceId": "<guid>",
  "runtimeStatus": "Completed",
  "output": "Email marked as spam: Contains prize scam indicators..."
}
```

## Project Structure

```
06_multi_agent_orchestration_conditionals/
├── function_app.py              # Orchestration with conditional logic
├── requirements.txt             # Python dependencies (includes pydantic)
├── host.json                    # Azure Functions config (DTS enabled)
├── local.settings.json.template # Local settings template
├── azure.yaml                   # Azure Developer CLI config
├── demo.http                    # HTTP test requests
├── README.md                    # This file
└── infra/                       # Bicep infrastructure
```

## Architecture

```
┌─────────────┐     ┌─────────────────────────────────────────────────────┐
│   Client    │────▶│                  Orchestration                      │
│             │     │  ┌────────────────────────────────────────────────┐ │
│  Email      │     │  │           SpamDetectionAgent                   │ │
│  Payload    │     │  │     Analyze email for spam content             │ │
└─────────────┘     │  └────────────────────┬───────────────────────────┘ │
                    │                       │                             │
                    │           ┌───────────┴───────────┐                 │
                    │           │    is_spam?           │                 │
                    │           ▼                       ▼                 │
                    │   ┌───────────────┐      ┌────────────────┐        │
                    │   │    YES        │      │      NO        │        │
                    │   │               │      │                │        │
                    │   │ handle_spam   │      │ EmailAssistant │        │
                    │   │   activity    │      │     Agent      │        │
                    │   └───────┬───────┘      └────────┬───────┘        │
                    │           │                       │                 │
                    │           │                       ▼                 │
                    │           │              ┌────────────────┐        │
                    │           │              │  send_email    │        │
                    │           │              │   activity     │        │
                    │           │              └────────┬───────┘        │
                    │           │                       │                 │
                    │           └───────────────────────┘                 │
                    │                       │                             │
                    │                       ▼                             │
                    │               ┌───────────────┐                    │
                    │               │    Return     │                    │
                    │               │    Result     │                    │
                    │               └───────────────┘                    │
                    └─────────────────────────────────────────────────────┘
```

## Key Concepts

### Structured Output with Pydantic
Agents return structured JSON responses validated by Pydantic models, enabling reliable conditional logic.

### Conditional Branching
The orchestration takes different paths based on the spam detection result, demonstrating dynamic workflow execution.

### Activity Functions
Side effects (spam handling, email sending) are encapsulated in activity functions, keeping the orchestration deterministic.
