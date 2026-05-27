# RGB BTCPay Plugin - Development Guide

## New Wallet Setup Flow (Regtest)

After creating a wallet in BTCPay UI:
1. Send 0.01 BTC to the wallet address (see below)
2. Mine 30 blocks
3. Hit Refresh in BTCPay UI
4. Press Create UTXOs (creates 4 UTXOs x 1000 sats for RGB)
5. Mine 30 more blocks + Refresh again

## Sending BTC to a New Wallet (Regtest)

Use the ThunderStack regtest mining API at `http://18.119.98.232:5000/execute`.

### Send BTC
```bash
curl -s -X POST "http://18.119.98.232:5000/execute" \
  -H "Content-Type: application/json" \
  -d '{"args": "sendtoaddress <ADDRESS> 0.01"}'
```

### Mine blocks to confirm
```bash
curl -s -X POST "http://18.119.98.232:5000/execute" \
  -H "Content-Type: application/json" \
  -d '{"args": "mine 30"}'
```

### If mining API returns "wallet does not exist" or electrum is unresponsive
Run the `start` command to reinitialize the regtest chain. This restarts bitcoind + electrs + proxy on the server (resets all state — wallets will need to be recreated):
```bash
curl -s -X POST "http://18.119.98.232:5000/execute" \
  -H "Content-Type: application/json" \
  -d '{"args": "start"}'
```
Then wait a few seconds before mining/sending.

## Paying an RGB Invoice (Regtest)

Uses the test wallet via local RGB Node (`http://127.0.0.1:8000`).

Test wallet headers (used in all requests):
```
xpub-van: tpubDCruH7eLXMYBeuYhZzqhehsPrZr8xsmGU5Sz4dtwJhZxU8UYaYCdMBZdY5norXcbE2sy4zJjhm7L475LcKKszgupYzPEPtMZ3viuWoHUVt5
xpub-col: tpubDCKNxAFWjzLHdaeMf1Vab93cVP4n8Mpt262Qk2rH5NnGme5GKZKh1JexxX4R9msn4afmWRgPwvrK38KJefTdGQ7UDRhXCjHu4SHgFWbCoN5
master-fingerprint: 11d94481
mnemonic: trophy hire lady move shuffle quit explain track praise twenty walnut awful
testnet address: tb1px34lgl4dsxarner72rkvgm6n2wq3xpjuzrdl079z4mlp9xktl32sl622sn
```

### Step 1: Get RGB invoice from BTCPay checkout page
```bash
curl -s "http://127.0.0.1:23002/i/<INVOICE_ID>" | grep -o 'rgb:[^"<[:space:]]*' | head -1
```

### Step 2: Build PSBT (sendbegin)
```bash
curl -s -X POST "http://127.0.0.1:8000/wallet/sendbegin" \
  -H "Content-Type: application/json" \
  -H "xpub-van: <XPUB_VAN>" -H "xpub-col: <XPUB_COL>" -H "master-fingerprint: <FP>" \
  -d '{"invoice": "<RGB_INVOICE>", "asset_id": "<ASSET_ID>", "amount": <AMOUNT>, "fee_rate": 2}'
```

### Step 3: Sign PSBT
```bash
curl -s -X POST "http://127.0.0.1:8000/wallet/sign" \
  -H "Content-Type: application/json" \
  -d '{"mnemonic": "<MNEMONIC>", "psbt": "<PSBT>", "xpub_van": "<XPUB_VAN>", "xpub_col": "<XPUB_COL>", "master_fingerprint": "<FP>"}'
```
Note: sign endpoint uses body params (underscores), not headers.

### Step 4: Broadcast (sendend)
```bash
curl -s -X POST "http://127.0.0.1:8000/wallet/sendend" \
  -H "Content-Type: application/json" \
  -H "xpub-van: <XPUB_VAN>" -H "xpub-col: <XPUB_COL>" -H "master-fingerprint: <FP>" \
  -d '{"signed_psbt": "<SIGNED_PSBT>"}'
```

### Step 5: Mine and settle
```bash
# Mine 60 blocks (RGB needs ~30+ confirmations)
for i in 1 2 3; do
  curl -s -X POST "http://18.119.98.232:5000/execute" \
    -H "Content-Type: application/json" -d '{"args": "mine 20"}'
  sleep 2
done

# Refresh sender wallet
curl -s -X POST "http://127.0.0.1:8000/wallet/refresh" \
  -H "xpub-van: <XPUB_VAN>" -H "xpub-col: <XPUB_COL>" -H "master-fingerprint: <FP>" \
  -d '{}'
```

Transfer statuses (rgb-lib SQLite `batch_transfer.status`): 0=WaitingCounterparty, 1=WaitingConfirmations, 2=WaitingConfirmations(consignment received), 3=Settled.
Keep mining + refreshing sender wallet until sender shows `updated_status: 2` (Settled), then wait for BTCPay's RGBInvoiceListener to refresh the receiver wallet (polls every 10s).

BTCPay invoice flow: New → Processing (when transfer detected at status 1 or 2) → Settled (when transfer reaches status 3).

### Test wallet setup (one-time after regtest reset)
1. Register: `POST /wallet/register` with headers
2. Fund: `sendtoaddress <wallet_address> 0.01` + mine 30
3. Sync: `POST /wallet/sync` with headers
4. Create UTXOs: `POST /wallet/createutxosbegin` → sign → `POST /wallet/createutxosend` + mine 30
5. Issue asset: `POST /wallet/issueassetnia` with `{"ticker": "TEST", "name": "Test Token", "precision": 0, "amounts": [10000]}`

## Generating RGB Invoice for Testing Send Asset

Uses the test wallet on RGB Node to create a blind receive invoice. The BTCPay wallet can then send assets to this invoice via the "Send RGB Asset" UI.

### Prerequisites
Test wallet must be registered, funded, and have UTXOs (see "Test wallet setup" above).
For testnet: set `NETWORK=1`, `INDEXER_URL=ssl://electrum.iriswallet.com:50013`, `PROXY_ENDPOINT=rpcs://proxy.iriswallet.com/0.2/json-rpc` in `rgb-node-local/.env` and restart.

### Generate invoice
```bash
curl -s -X POST "http://127.0.0.1:8000/wallet/blindreceive" \
  -H "Content-Type: application/json" \
  -H "xpub-van: <XPUB_VAN>" -H "xpub-col: <XPUB_COL>" -H "master-fingerprint: <FP>" \
  -d '{"asset_id": null, "amount": 100, "duration_seconds": 7200, "min_confirmations": 1}'
```
Returns `{"invoice":"rgb:~/~/...","recipient_id":"...","expiration_timestamp":...}`

Paste the `invoice` value into the "RGB Invoice" field on Send RGB Asset page.

### After sending: refresh receiver to trigger settlement
```bash
curl -s -X POST "http://127.0.0.1:8000/wallet/refresh" \
  -H "xpub-van: <XPUB_VAN>" -H "xpub-col: <XPUB_COL>" -H "master-fingerprint: <FP>" \
  -d '{}'
```
Then sync the BTCPay wallet (Refresh button in UI) to pick up the ACK and move to Settled.

## "non-final" Broadcast Error Fix

If Create UTXOs fails with `FailedBroadcast: "non-final"`:
- The wallet's BDK state is stale (chain was reset or many blocks mined since wallet creation)
- Fix: delete wallet data from disk + DB, restart BTCPay, recreate wallet on the fresh chain
- Wallet data dir: `~/.btcpayserver/RegTest/RegTest/rgb-wallets/<wallet-id>/`
- DB: `DELETE FROM "RGB_Wallets";`
- Always delete `~/.btcpayserver/Plugins/commands` before restart (plugin auto-disable)

## BTCPay Server Credentials

```
Email: NikMaslukov@gmail.com
Password: testpass123
Store ID: 8Ekf6LNVpHhP2EMzipJY65zodrCsTZMgUUQtRGpnhumH
Store Name: Dwadwad
```

Ports: testnet=23001, regtest=23002 (depends on `BTCPAY_NETWORK`).

### BTCPay Testnet RGB Wallet (active, plugin-managed)
```
Wallet ID: 009e3b9c-d1fa-44e9-920f-23ded5a61b78
Network: testnet
Master Fingerprint: b6785850
XpubVanilla: tpubDDKKPYVSyTn4F2j4rVsKmbhSA6yJyTHMp3kvsL4b7fBqbjo6FQiYPyXVCAxyMosPyTmRkSazF79Jm8qePyvQBY7joAWCfEjb5fqJR4iHW1T
XpubColored: tpubDDQGDx4oEhgaMFtgNQTLpJMsRhsEx1bJtfCGyfGNQS2PUj7JP2UKCa1pnEy84CKy9QXyNdT67sg4t71sQkyLDc6u4yCTz46zmRA6kroL4ZX
Mnemonic: notice spawn meadow rent buzz light vibrant dinner muffin visit gun bench
```

### Creating an invoice via API
```bash
curl -s -X POST "http://127.0.0.1:23001/api/v1/stores/8Ekf6LNVpHhP2EMzipJY65zodrCsTZMgUUQtRGpnhumH/invoices" \
  -H "Content-Type: application/json" \
  -H "Authorization: token a24ca2c69da31c10cf393cab0d838ecceb86dd17" \
  -d '{"amount": 33, "currency": "RGB", "checkout": {"expirationMinutes": 60}}'
```

### Creating a new API key (if needed)
```bash
curl -s -X POST "http://127.0.0.1:23001/api/v1/api-keys" \
  -u "NikMaslukov@gmail.com:testpass123" \
  -H "Content-Type: application/json" \
  -d '{"permissions": ["unrestricted"]}'
```

### If account gets locked out
```sql
-- Run against btcpay postgres (port 6512)
UPDATE "AspNetUsers" SET "LockoutEnd" = NULL, "AccessFailedCount" = 0
WHERE "Email" = 'NikMaslukov@gmail.com';
```

## Local bitcoind + NBXplorer (optional, for BTC-CHAIN)

Required only if the store has BTC-CHAIN payment method enabled. Without NBXplorer, BTCPay shows "Your nodes are syncing" and the dashboard crashes.

### Start bitcoind
```bash
mkdir -p /tmp/btc-regtest
bitcoind -regtest -datadir=/tmp/btc-regtest -server -rpcuser=btcpay -rpcpassword=btcpay -rpcport=18443 -fallbackfee=0.0002 -daemon
bitcoin-cli -regtest -datadir=/tmp/btc-regtest -rpcuser=btcpay -rpcpassword=btcpay -rpcport=18443 createwallet "default"
bitcoin-cli -regtest -datadir=/tmp/btc-regtest -rpcuser=btcpay -rpcpassword=btcpay -rpcport=18443 -generate 1
```

### Start NBXplorer
```bash
cd /Users/nikitam/projects/nbxplorer && /opt/homebrew/Cellar/dotnet/10.0.105/bin/dotnet run --project NBXplorer -- \
  --network=regtest \
  --btcrpcurl=http://127.0.0.1:18443 \
  --btcrpcuser=btcpay \
  --btcrpcpassword=btcpay \
  --btcnodeendpoint=127.0.0.1:18444 \
  --postgres="User ID=postgres;Host=127.0.0.1;Port=6512;Database=nbxplorer;Password=postgres" \
  --port=24446 \
  --noauth
```

Note: bitcoind v31 may have P2P protocol issues with NBXplorer — node disconnects during handshake. If this happens, the RGB plugin still works (uses Electrum directly), but the BTCPay dashboard will show sync warnings.

## Full End-to-End Testing Flow

1. Start docker containers: `docker start btcpay-postgres rgb-node-local-thunderlink-python-1`
2. Build: `dotnet build BTCPayServer.Plugins.RgbUtexo.csproj -c Debug`
3. Start BTCPay (see start command in MEMORY.md)
4. Create invoice via API (see above) or BTCPay UI
5. Extract RGB invoice: `curl -s "http://127.0.0.1:23002/i/<INVOICE_ID>" | grep -o 'rgb:[^"<[:space:]]*' | head -1`
6. Pay via test wallet: sendbegin → sign → sendend (see "Paying an RGB Invoice" section)
7. Mine 2 blocks → verify BTCPay invoice status = **Processing**
8. Mine 60+ blocks, refresh sender wallet → verify BTCPay invoice status = **Settled**
9. Check DB: `SELECT "Id", "Status" FROM "Invoices" WHERE "Id" = '<INVOICE_ID>';`
10. Check payment: `SELECT "Id", "Status", "Amount" FROM "Payments" WHERE "InvoiceDataId" = '<INVOICE_ID>';`

## RGB Node (Local)

Cloned from `https://github.com/RGB-OS/RGB-Node.git` branch `public` into `rgb-node-local/`.

Start: `cd rgb-node-local && docker compose up --build -d`

Env vars (in `rgb-node-local/.env`):
- `NETWORK=4` (regtest — enum: 0=Mainnet, 1=Testnet, 2=Testnet4, 3=Signet, 4=Regtest)
- `INDEXER_URL=tcp://regtest.thunderstack.org:50001`
- `PROXY_ENDPOINT=rpc://regtest.thunderstack.org:3000/json-rpc`

Runs on `http://127.0.0.1:8000`.

### Commit and push
Never commit with Opus as co-author, do not reveal yourself
Always push with: `GIT_SSH_COMMAND="ssh -i ~/.ssh/nikmaslukov-ssh -o IdentitiesOnly=yes" git push`

## Subagent Usage Guide

### Principle
Subagents run in isolated context — their tool calls, diffs, and logs stay out of the main conversation. Use them to keep the primary context lean.

### When to delegate
- **Exploration/search** — finding files, grepping patterns, reading multiple files to answer a question
- **Code review** — analyzing uncommitted diffs or PR changes
- **Build/test** — running builds, test suites, checking logs for errors
- **Research** — reading docs, web searches, synthesizing information
- **Parallel independent tasks** — send multiple Agent calls in one message

### When NOT to delegate
- Quick single-file edits (overhead > benefit)
- Sequential dependent changes (subagents can't see each other's work)
- Tasks where you need the raw output to make the next decision inline

### Prompt best practices
1. **Self-contained** — subagent has zero context from this conversation. Include: goal, relevant file paths, what you already know, what format to return
2. **Specify output format** — "return pass/fail + key errors only", "report in under 200 words", "list files changed + one-line summary each"
3. **Don't dump context** — only include what's needed for that specific task
4. **Name the action** — "review", "search", "build and report" — not vague "help with"

### Patterns

**Explore agent (read-only, fast):**
```
Agent(subagent_type="Explore", description="Find RGB invoice handling", prompt="Find all files that handle RGB invoice creation. List file:line for each entry point. Under 100 words.")
```

**Review agent (read-only, thorough):**
```
Agent(description="Review uncommitted changes", prompt="Run git diff on staged+unstaged changes. Check for: security issues, null refs, missing error handling, breaking API changes. Report issues only, skip clean code. Under 300 words.")
```

**Build/test agent (background):**
```
Agent(description="Build and test", run_in_background=true, prompt="Build with `dotnet build ...`. If it fails, report errors. If it passes, report success + any warnings.")
```

**Parallel agents (one message, multiple calls):**
Send multiple Agent calls simultaneously for independent tasks — e.g., one explores frontend, another reviews backend.

### Context protection rules
- **Never ask subagent to return full diffs** — ask for summaries
- **Never paste subagent results into main context** — relay a concise summary to the user
- **Use `run_in_background=true`** for long tasks — you get notified on completion without blocking
- **Use `isolation="worktree"`** when subagent needs to edit files without affecting your working tree

## Lockfile maintenance

The plugin and test projects use `<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>` and have committed `packages.lock.json` files. The test project has a `<ProjectReference>` to the plugin, so the test lockfile captures packages flowing through the plugin → submodule graph.

- Submodule (`submodules/btcpayserver`) update: regenerate BOTH lockfiles
  ```bash
  dotnet restore BTCPayServer.Plugins.RgbUtexo.csproj --force-evaluate
  dotnet restore BTCPayServer.Plugins.RgbUtexo.Tests/BTCPayServer.Plugins.RgbUtexo.Tests.csproj --force-evaluate
  ```
- Plugin's own package change: regenerate both (the test lockfile captures plugin transitives).
- Test project's own package change: regenerate only the test lockfile.
- Always commit the regenerated lockfile alongside the change that triggered it. CI / local verification: `dotnet restore --locked-mode` fails on drift.