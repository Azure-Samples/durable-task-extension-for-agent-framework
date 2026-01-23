# 08 - MCP Server

This sample demonstrates how to configure AI agents with different trigger configurations, enabling agents to be accessible via HTTP endpoints, Model Context Protocol (MCP) tools, or both.

## Overview

The sample creates three agents with different accessibility configurations:
- **Joker**: HTTP trigger only (default behavior)
- **StockAdvisor**: MCP tool trigger only (HTTP disabled)
- **PlantAdvisor**: Both HTTP and MCP tool triggers enabled

It demonstrates:
- Flexible agent registration with customizable trigger configurations
- Model Context Protocol (MCP) tool integration
- Multi-trigger agent configurations

## What is MCP?

The [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) is an open standard that enables AI models to securely access external tools and data sources. By enabling MCP triggers on agents, they can be invoked by MCP-compatible clients like Claude Desktop, VS Code with Copilot, and other AI assistants.

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

### HTTP Endpoints

#### Health Check
```http
GET /api/health
```

#### Joker Agent (HTTP Only)
```http
POST /api/agents/Joker/run
Content-Type: application/json

{
  "message": "Tell me a programming joke.",
  "thread_id": "joker-001"
}
```

#### PlantAdvisor Agent (HTTP + MCP)
```http
POST /api/agents/PlantAdvisor/run
Content-Type: application/json

{
  "message": "What plant is good for low light?",
  "thread_id": "plant-001"
}
```

#### StockAdvisor Agent (MCP Only)
> Note: This agent's HTTP endpoint is disabled. It can only be invoked via MCP protocol.

### MCP Integration

To use agents via MCP, configure your MCP client to connect to the Azure Functions host:

```json
{
  "mcpServers": {
    "agent-functions": {
      "url": "http://localhost:7071/mcp"
    }
  }
}
```

## Project Structure

```
08_mcp_server/
├── function_app.py              # Agent definitions with trigger configs
├── requirements.txt             # Python dependencies
├── host.json                    # Azure Functions config (DTS enabled)
├── local.settings.json.template # Local settings template
├── azure.yaml                   # Azure Developer CLI config
├── demo.http                    # HTTP test requests
├── README.md                    # This file
└── infra/                       # Bicep infrastructure
```

## Agent Configuration Reference

### Default (HTTP only)
```python
app.add_agent(agent)  # HTTP enabled, MCP disabled
```

### HTTP Only (explicit)
```python
app.add_agent(agent, enable_http_endpoint=True, enable_mcp_tool_trigger=False)
```

### MCP Only
```python
app.add_agent(agent, enable_http_endpoint=False, enable_mcp_tool_trigger=True)
```

### Both HTTP and MCP
```python
app.add_agent(agent, enable_http_endpoint=True, enable_mcp_tool_trigger=True)
```

## Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                        Azure Functions Host                         │
│                                                                     │
│  ┌───────────────┐   ┌───────────────┐   ┌───────────────┐        │
│  │    Joker      │   │ StockAdvisor  │   │ PlantAdvisor  │        │
│  │   (HTTP)      │   │   (MCP)       │   │ (HTTP + MCP)  │        │
│  └───────┬───────┘   └───────┬───────┘   └───────┬───────┘        │
│          │                   │                   │                 │
│  ┌───────▼───────────────────▼───────────────────▼───────┐        │
│  │              AgentFunctionApp                          │        │
│  └───────────────────────────────────────────────────────┘        │
│                              │                                     │
└──────────────────────────────┼─────────────────────────────────────┘
                               │
              ┌────────────────┼────────────────┐
              ▼                ▼                ▼
       ┌──────────┐     ┌──────────┐     ┌──────────┐
       │   HTTP   │     │   MCP    │     │  Azure   │
       │ Clients  │     │ Clients  │     │  OpenAI  │
       └──────────┘     └──────────┘     └──────────┘
```

## Key Concepts

### Flexible Trigger Configuration
Each agent can be configured independently with different trigger types, allowing fine-grained control over how agents are accessed.

### MCP Tool Triggers
Agents with MCP triggers can be discovered and invoked by MCP-compatible AI assistants, enabling agentic workflows where one AI can call another.

### Combined Access Patterns
Agents like PlantAdvisor can serve both traditional HTTP clients and MCP clients, maximizing accessibility.
