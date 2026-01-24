# Durable Agents for Azure Functions - Samples

This repository contains samples demonstrating how to build **durable AI agents** using Azure Functions with the [Microsoft Agent Framework](https://github.com/microsoft/agent-framework).

All samples are complete with **Azure Developer CLI (azd)** and **Bicep** infrastructure for one-command deployment to Azure.

## 🚀 Quick Start

```bash
# Navigate to any sample
cd quickstarts/python/azure-functions/01_single_agent

# Deploy to Azure
azd auth login
azd up
```

## 📁 Samples

| Sample | Description |
|--------|-------------|
| [01_single_agent](quickstarts/python/azure-functions/01_single_agent) | Basic single agent with Azure OpenAI |
| [02_multi_agent](quickstarts/python/azure-functions/02_multi_agent) | Multiple agents working together |
| [03_reliable_streaming](quickstarts/python/azure-functions/03_reliable_streaming) | Redis-backed streaming with disconnect/resume support |
| [04_single_agent_orchestration_chaining](quickstarts/python/azure-functions/04_single_agent_orchestration_chaining) | Durable orchestration with sequential agent chaining |
| [05_multi_agent_orchestration_concurrency](quickstarts/python/azure-functions/05_multi_agent_orchestration_concurrency) | Parallel agent execution with fan-out/fan-in |
| [06_multi_agent_orchestration_conditionals](quickstarts/python/azure-functions/06_multi_agent_orchestration_conditionals) | Conditional routing between agents |
| [07_single_agent_orchestration_hitl](quickstarts/python/azure-functions/07_single_agent_orchestration_hitl) | Human-in-the-loop approval workflows |
| [08_mcp_server](quickstarts/python/azure-functions/08_mcp_server) | Model Context Protocol (MCP) server integration |

## 🏗️ Architecture

Each sample uses:

- **Azure Functions (Flex Consumption)** - Serverless compute with Python 3.11
- **Durable Task Scheduler (DTS)** - Reliable orchestration and state management
- **Azure OpenAI** - LLM capabilities (GPT-4o-mini)
- **Managed Identity** - Secure, keyless authentication
- **Application Insights** - Monitoring and observability

## 📋 Prerequisites

- [Azure Developer CLI (azd)](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd)
- [Azure Functions Core Tools 4.x](https://learn.microsoft.com/azure/azure-functions/functions-run-local)
- [Python 3.11+](https://www.python.org/downloads/)
- An Azure subscription

### Local Development

For local development, you'll also need:
- [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite) - Local Azure Storage emulator
- [Docker](https://www.docker.com/) - For running the DTS emulator locally

## 🛠️ Sample Structure

Each sample follows a consistent structure:

```
sample_name/
├── function_app.py              # Main Azure Functions app
├── host.json                    # Functions host configuration (DTS enabled)
├── requirements.txt             # Python dependencies
├── local.settings.json.template # Local settings template
├── demo.http                    # HTTP test requests
├── azure.yaml                   # Azure Developer CLI configuration
├── README.md                    # Sample-specific documentation
└── infra/                       # Bicep infrastructure
    ├── main.bicep               # Main infrastructure template
    ├── main.parameters.json     # Parameters file
    ├── abbreviations.json       # Resource naming conventions
    └── app/                     # App-specific modules
        └── dts.bicep            # Durable Task Scheduler
```

## 🔐 Security

All samples use **Managed Identity** for authentication:
- No API keys or connection strings in code
- Azure RBAC for resource access
- Entra ID authentication for Redis (where applicable)

## 📚 Learn More

- [Microsoft Agent Framework Documentation](https://github.com/microsoft/agent-framework)
- [Azure Functions Documentation](https://learn.microsoft.com/azure/azure-functions/)
- [Durable Task Scheduler](https://learn.microsoft.com/azure/azure-functions/durable/durable-functions-task-scheduler)
- [Azure Developer CLI](https://learn.microsoft.com/azure/developer/azure-developer-cli/)

## 🤝 Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

## 📄 License

See [LICENSE.md](LICENSE.md) for details.
