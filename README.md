ACC-SHOOTER 🎮

A Unity-based multiplayer First-Person Shooter (FPS) with dedicated game-server hosting and AWS cloud infrastructure.

ACC-SHOOTER is a 5v5 Team Deathmatch project built with Unity and C#. It combines real-time multiplayer gameplay with a dedicated Linux game server and an AWS backend designed to support matchmaking, room management, and server coordination.

The project explores how cloud infrastructure can be used to host multiplayer game sessions independently of individual players' computers.

🎯 Project Objectives

- Develop a real-time multiplayer FPS using Unity.
- Support team-based gameplay with synchronized player states.
- Host multiplayer matches on a dedicated Linux server.
- Build an AWS backend for matchmaking and room management.
- Integrate the game client, backend services, and dedicated server.
- Explore scalable and reliable cloud-hosted multiplayer architecture.

🎮 Gameplay Features

- 5v5 Team Deathmatch design, supporting up to 10 players.
- First-person gameplay.
- Multiplayer player movement and synchronization.
- Shooting, damage, health, death, and respawn mechanics.
- Team balancing and team-based scoring.
- Friendly fire disabled.
- Configurable kills-to-win match objective.
- Room-code-based joining flow.
- Scoreboard and kill feed.
- Top-killer display and match-winner screen.
- Unity New Input System integration.

Feature availability may vary as development continues.

🛠️ Technology Stack

TECHNOLOGY STACK

Technology                  Purpose
--------------------------  ------------------------------------------
Unity                       Game development and client runtime
C#                          Gameplay and networking logic
Netcode for GameObjects     Multiplayer networking
Unity Transport             Network communication
Linux                       Dedicated game-server environment
Amazon EC2                  Intended cloud host for the game server
Amazon API Gateway           HTTP API entry point
AWS Lambda                  Backend logic and matchmaking
Amazon DynamoDB              Backend data storage
AWS SAM                     Backend infrastructure definition
Python                      Dedicated-server agent and supporting tools
Git                         Version control
GitHub                      Source code hosting and collaboration

🏗️ System Architecture

The intended cloud architecture separates gameplay from backend coordination.

                 UNITY GAME CLIENTS
                  /             \
                 /               \
        AWS BACKEND             GAME SERVER
             |                       |
       API Gateway              Linux Server
             |                  on Amazon EC2
          Lambda                     |
             |                       |
         DynamoDB <--- Server Agent -+

Main Components

1. Unity Game Client

Runs on each player's computer and handles rendering, input, gameplay UI, and client-side interactions.

2. AWS Backend

Provides the foundation for matchmaking, room management, server discovery, and related game services through API Gateway, Lambda, and DynamoDB.

3. Linux Dedicated Game Server

Runs the multiplayer session independently of the players' computers. Clients connect to the dedicated server for real-time gameplay.

4. Dedicated-Server Agent

A Python service designed to help register and manage game servers, report server health, and coordinate with backend services.

5. AWS Infrastructure

AWS services provide the backend environment, while EC2 is intended to host the dedicated game server.

This diagram represents the target architecture. The complete AWS-to-game-client flow still requires end-to-end verification.

📂 Repository Structure

ACC-SHOOTER/
├── game/
│   ├── Assets/
│   │   └── Scripts/
│   ├── Packages/
│   ├── ProjectSettings/
│   └── README_AWS_CLOUD_HANDOVER.md
│
├── cloud/
│   ├── cloud/
│   │   ├── acc-shooter-api/
│   │   │   ├── template.yaml
│   │   │   └── hello_world/
│   │   └── agent/
│   │       ├── agent.py
│   │       └── deploy/
│   └── docs/
│       └── ec2_deployment.md
│
├── .gitignore
└── README.md

🚀 Getting Started

Prerequisites

- Git
- The Unity Editor version specified in "game/ProjectSettings/ProjectVersion.txt"
- Python 3 and pip for backend development
- AWS CLI and AWS SAM CLI for AWS backend workflows
- Appropriate AWS credentials for authorized cloud operations

1. Clone the Repository

git clone https://github.com/ArhamBothra-work/ACC-SHOOTER.git
cd ACC-SHOOTER

2. Open the Unity Project

1. Open Unity Hub.
2. Select Add Project from Disk.
3. Choose the "game/" directory.
4. Open the project using the version specified in "ProjectVersion.txt".
5. Allow Unity to resolve packages before running the game.

Unity-generated directories, including "Library/", are excluded from version control and will be regenerated locally.

3. Explore the Cloud Backend

The backend source is located in:

cloud/cloud/acc-shooter-api/

The dedicated-server agent is located in:

cloud/cloud/agent/

Deployment documentation is available at:

cloud/docs/ec2_deployment.md

Review the deployment guide and AWS SAM template before running cloud commands. Deployment can create billable AWS resources.

🧪 Development and Testing Status

Component| Current Status
Unity gameplay| Implemented; local testing performed
Dedicated Linux server| Tested locally
Multiple Unity clients| Local multiplayer demonstrated
AWS backend source| Present; deployed parity requires verification
EC2 dedicated-server deployment| End-to-end operation not yet confirmed
AWS matchmaking to live game session| End-to-end flow not yet confirmed
Production readiness| Further security, reliability, and deployment work required

The local multiplayer tests demonstrate the basic client-to-dedicated-server connection. They do not, by themselves, establish that the full AWS deployment is operational.

🔐 Security and Deployment Notes

- Never commit AWS access keys, private keys, passwords, or real environment files.
- Keep credentials out of the Unity client and all distributed game builds.
- Development-only tickets and local-test modes must not be treated as production authentication.
- Validate matchmaking tickets and player requests on the server side.
- Review API authorization, rate limiting, logging, and monitoring before public deployment.
- Monitor AWS usage and shut down test infrastructure when it is no longer required.

🗺️ Roadmap

- [x] Develop the Unity multiplayer gameplay foundation.
- [x] Test a dedicated Linux server locally.
- [x] Demonstrate multiple clients joining the same local session.
- [x] Consolidate game and cloud source into a unified repository.
- [ ] Verify a clean setup from a fresh repository clone.
- [ ] Run and validate backend and agent test suites.
- [ ] Verify the deployed AWS backend against the current source.
- [ ] Deploy the dedicated game server to Amazon EC2.
- [ ] Verify server registration, health reporting, and matchmaking.
- [ ] Test the complete flow from matchmaking to joining a live game.
- [ ] Improve security, observability, and deployment documentation.

👥 Contributors

Developed as a collaborative project combining game development, networking, backend engineering, and cloud infrastructure.

📄 License

No license has been specified yet. Until a license is added, the repository should be treated as all rights reserved.
