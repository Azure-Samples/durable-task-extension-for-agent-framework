# 07 - Single Agent Orchestration with Human-in-the-Loop (HITL)

This sample demonstrates a human-in-the-loop workflow where an agent generates content that requires human approval before publishing, with support for iterative feedback and revision cycles.

## Overview

The sample creates a content generation pipeline with:
- **WriterAgent**: Generates articles on specified topics
- **Human Approval**: Workflow pauses for human review
- **Iterative Refinement**: Content is regenerated based on feedback

It demonstrates:
- External events for human interaction with orchestrations
- Configurable approval timeouts
- Iterative content refinement loop
- Custom workflow status tracking

## How It Works

1. User submits a topic for content generation
2. WriterAgent generates an initial article
3. Workflow notifies human and **waits for approval**
4. **If approved**: Content is published
5. **If rejected**: Agent rewrites based on feedback, returns to step 3
6. **If timeout**: Workflow fails with timeout error

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

### 1. Start the Orchestration
```http
POST /api/hitl/run
Content-Type: application/json

{
  "topic": "The Future of Artificial Intelligence",
  "max_review_attempts": 3,
  "approval_timeout_hours": 24
}
```

### 2. Check Status (Waiting for Approval)
```http
GET /api/hitl/status/{instanceId}
```

Response:
```json
{
  "instanceId": "<guid>",
  "runtimeStatus": "Running",
  "workflowStatus": "Requesting human feedback. Iteration #1. Timeout: 24 hour(s)."
}
```

### 3. Approve the Content
```http
POST /api/hitl/approve/{instanceId}
Content-Type: application/json

{
  "approved": true,
  "feedback": "Great article!"
}
```

### 3. Or Reject with Feedback
```http
POST /api/hitl/approve/{instanceId}
Content-Type: application/json

{
  "approved": false,
  "feedback": "Please add more examples and technical depth."
}
```

### 4. Check Final Status
```http
GET /api/hitl/status/{instanceId}
```

Response (when published):
```json
{
  "instanceId": "<guid>",
  "runtimeStatus": "Completed",
  "workflowStatus": "Content published successfully at 2024-01-01T12:00:00",
  "output": {
    "content": "..."
  }
}
```

## Project Structure

```
07_single_agent_orchestration_hitl/
├── function_app.py              # HITL orchestration logic
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
┌─────────────┐     ┌──────────────────────────────────────────────────────────┐
│   Client    │────▶│                    Orchestration                         │
│             │     │  ┌──────────────────────────────────────────────────────┐│
└─────────────┘     │  │              Generate Initial Content                ││
       │            │  │                  (WriterAgent)                        ││
       │            │  └─────────────────────────┬────────────────────────────┘│
       │            │                            │                             │
       │            │                            ▼                             │
       │            │  ┌──────────────────────────────────────────────────────┐│
       │            │  │           Notify Human & Wait for Event              ││
       │            │  │        (wait_for_external_event + timeout)           ││
       │            │  └─────────────────────────┬────────────────────────────┘│
       │            │                            │                             │
       │            │           ┌────────────────┼────────────────┐            │
       │            │           ▼                ▼                ▼            │
       │            │    ┌──────────┐     ┌──────────┐     ┌──────────┐       │
       │            │    │ APPROVED │     │ REJECTED │     │ TIMEOUT  │       │
       │            │    └────┬─────┘     └────┬─────┘     └────┬─────┘       │
       │            │         │                │                │             │
       │            │         ▼                ▼                ▼             │
       │            │    ┌──────────┐     ┌──────────┐     ┌──────────┐       │
       │            │    │ Publish  │     │ Rewrite  │     │  Fail    │       │
       │            │    │ Content  │     │ Content  │─────│ (Error)  │       │
       │            │    └────┬─────┘     └────┬─────┘     └──────────┘       │
       │            │         │                │                              │
       │            │         │                └───────── (back to wait)      │
       │            │         ▼                                               │
       │            │    ┌──────────┐                                         │
       │◀───────────│────│  Return  │                                         │
                    │    │  Output  │                                         │
                    │    └──────────┘                                         │
                    └──────────────────────────────────────────────────────────┘
```

## Key Concepts

### External Events
The orchestration uses `wait_for_external_event` to pause execution until a human provides approval via the `/approve` endpoint.

### Timeout with Task Racing
`task_any` races the approval event against a timer, allowing configurable timeout behavior.

### Iterative Refinement
If rejected, the agent receives feedback and regenerates content, creating a review loop until approval or max attempts.

### Custom Status
`set_custom_status` provides real-time workflow state visibility through the status endpoint.
