# EC2 Deployment Guide for ACC Shooter Dedicated Server

## Overview
This document describes how to deploy the **ACC Shooter** dedicated‑server agent on an Amazon Linux 2023 EC2 instance. The agent runs the Unity headless server binary (`ACCServer.x86_64`), registers the slot with the AWS backend, sends periodic heartbeats, and exposes a local HTTP API for the Unity client.

---

## Prerequisites
1. **Amazon Linux 2023** (or a compatible distro with Python 3.11).
2. **IAM instance role** attached to the EC2 instance.
3. **Security‑group** with:
   * UDP **7777** inbound (the game port). Adjust the CIDR as needed.
   * Outbound HTTPS (port 443) to reach the backend API.
   * Optional inbound SSH (22) for administration.
4. **Elastic IP / Public DNS** (or rely on IMDS public IP).
5. The **Linux server binary** built from Unity (`ACCServer.x86_64`). Place it at the path you will configure in `acc-agent.env` (default `/opt/acc/ACCServer.x86_64`).

---

## IAM Role Permissions
The agent authenticates to the backend using **SigV4** with the instance role's credentials. The only AWS permission required is the ability to invoke the backend API:

```json
{
  "Effect": "Allow",
  "Action": "execute-api:Invoke",
  "Resource": "arn:aws:execute-api:<region>:<account-id>:<api-id>/*"
}
```
*Replace `<region>`, `<account-id>`, and `<api-id>` with the values from your SAM `template.yaml` (the `BackendFunction`'s API).*

No other AWS APIs (e.g., `ec2:DescribeInstances`) are called by the agent; the public IP is obtained via the EC2 Instance Metadata Service (IMDS) using plain HTTP, which does not require extra permissions.

---

## Deployment Steps
1. **Build the Unity Linux server binary**
   * In Unity, open *Build Settings* → *Linux Server* → *Build*.
   * Copy the resulting `ACCServer.x86_64` to the EC2 instance, e.g. `/opt/acc/ACCServer.x86_64`.
2. **Copy deployment files** to the instance (e.g., via `scp`):
   ```bash
   scp -r cloud/agent/deploy ec2-user@<instance-ip>:/home/ec2-user/
   ```
3. **Create and edit the environment file**
   ```bash
   sudo cp /home/ec2-user/deploy/acc-agent.env.example /etc/acc-agent.env
   sudo nano /etc/acc-agent.env   # adjust values
   ```
   At minimum set:
   * `API_URL` – backend base URL (default is fine).
   * `AWS_REGION` – region of the backend.
   * `SERVER_BINARY` – absolute path to `ACCServer.x86_64`.
   * `PUBLIC_ADDRESS` – optional; if left empty the agent will use the public IP from IMDS.
4. **Run the bootstrap script**
   ```bash
   sudo bash /home/ec2-user/deploy/install.sh
   ```
   The script validates the environment, installs Python dependencies, copies `agent.py` and the server binary to `/opt/acc/`, installs the systemd unit, and starts the service.
5. **Verify the service**
   ```bash
   sudo systemctl status acc-agent.service
   sudo journalctl -u acc-agent -f   # watch logs
   ```
   The service should report `STARTING` → `READY` and the loopback endpoint should be listening on `127.0.0.1:8081`.
6. **Test connectivity** (optional)
   From the EC2 instance you can issue a quick curl request:
   ```bash
   curl http://127.0.0.1:8081/status
   ```
   It should return JSON with the agent status and server info.

---

## Manual Launch Checklist
- [ ] Linux server binary exists and is executable.
- [ ] `/etc/acc-agent.env` contains all required variables.
- [ ] Systemd unit `acc-agent.service` is enabled and active.
- [ ] Security‑group allows UDP 7777 inbound and outbound HTTPS.
- [ ] IAM role includes the `execute-api:Invoke` permission for the backend API.

---

## Troubleshooting
| Symptom | Likely cause | Fix |
|---------|--------------|-----|
| Service fails to start, log shows *Server binary not found* | `SERVER_BINARY` path incorrect or file missing | Verify the binary location and permissions; update `SERVER_BINARY` in `/etc/acc-agent.env`.
| Heartbeat errors / `SigV4HttpError` | IAM role missing `execute-api:Invoke` permission | Add the permission to the instance role policy.
| Loopback endpoint not reachable | `AGENT_PORT` set to 0 or firewall blocks localhost traffic | Ensure `AGENT_PORT` > 0 (default 8081) and that the port is not used by another process.

---

*This guide assumes no production‑grade hardening is required for the university prototype. Adjust security settings as appropriate for your environment.*
