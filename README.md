# RGB BTCPay Server Plugin

> **Beta notice:** This package is currently in beta. Please test thoroughly in development environments before using in production.

Accept RGB asset payments (tokens, stablecoins) in BTCPay Server.

[![BTCPay Server](https://img.shields.io/badge/BTCPay%20Server-Plugin-brightgreen)](https://btcpayserver.org)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-blue)](https://dotnet.microsoft.com)

## Features

- Accept RGB20 token payments alongside Bitcoin
- Issue new RGB assets directly from BTCPay
- Two-step invoice settlement (Processing → Settled) matching BTCPay's native Bitcoin flow
- Full UTXO management for RGB allocations
- BTC transaction history for wallet operations
- Configurable minimum confirmations for payment settlement
- Native rgb-lib integration (no external RGB Node required)

## Installation

### Via Plugin Builder (Recommended)

1. Go to your BTCPay Server **Settings** → **Plugins**
2. Search for "RGB Payments"
3. Click **Install**
4. Restart BTCPay Server

### Manual Installation

1. Download the latest release from the [Plugin Builder](https://plugin-builder.btcpayserver.org/public/plugins)
2. Extract to your BTCPay Server plugins directory
3. Restart BTCPay Server

## Configuration

### Environment Variables

```bash
# Electrum server for blockchain data
RGB_ELECTRUM_URL=ssl://electrum.blockstream.info:60002

# Directory for RGB wallet data (default: <btcpay-data-dir>/<network>/rgb-wallets)
RGB_DATA_DIR=/data/rgb-wallets

# RGB proxy endpoint for consignment exchange
RGB_PROXY_ENDPOINT=rpc://proxy.iriswallet.com:443/json-rpc
```

### Configuration File

Alternatively, create `rgb.json` in your BTCPay Server data directory:
```json
{
  "network": "mainnet",
  "electrum_url": "ssl://electrum.blockstream.info:60002",
  "rgb_data_dir": "/data/rgb-wallets",
  "proxy_endpoint": "rpc://proxy.iriswallet.com:443/json-rpc"
}
```

### Network Defaults

| Network | Default Electrum URL | Proxy Endpoint |
|---------|---------------------|----------------|
| Mainnet | ssl://electrum.blockstream.info:60002 | rpc://proxy.iriswallet.com:443/json-rpc |
| Testnet | ssl://electrum.blockstream.info:60002 | rpc://proxy.iriswallet.com:443/json-rpc |
| Regtest | tcp://127.0.0.1:50001 (local electrs) | rpc://regtest.thunderstack.org:3000/json-rpc |

## User Guide

### Step 1: Create an RGB Wallet

1. Open your BTCPay Server store
2. In the left sidebar, click **RGB Wallet**
3. You will land on the **Setup** page
4. Enter a wallet name (e.g. "My RGB Wallet")
5. Select the network (Mainnet, Testnet, or Regtest)
6. Click **Create Wallet**

The plugin generates a new wallet with two keypairs: one for regular BTC transactions and one for RGB (colored) operations. The mnemonic is encrypted and stored securely within BTCPay.

### Step 2: Fund the Wallet with BTC

RGB operations require on-chain Bitcoin for transaction fees and UTXO creation.

1. On the **RGB Wallet** dashboard, copy the wallet address shown in the "Wallet Address" card
2. Send a small amount of BTC to this address (0.01 BTC is enough for regtest/testnet)
3. Wait for the transaction to confirm (30+ blocks on regtest)
4. Click the **Refresh** button on the dashboard to update balances

You should see your BTC balance update in the "BTC Balance" card.

### Step 3: Create Colorable UTXOs

RGB assets are stored on special "colorable" UTXOs. You need to create them before you can receive any RGB payments.

1. On the dashboard, click **Manage UTXOs** (or navigate to **UTXOs** in the sidebar)
2. Click the **Create UTXOs** button
3. Wait for the transaction to confirm (30+ blocks on regtest)
4. Go back to the dashboard and click **Refresh**

The "Colorable UTXOs" card on the dashboard should now show a non-zero count. Each UTXO can hold multiple RGB allocations.

### Step 4: Issue an RGB Asset (Optional)

If you want to create your own token:

1. On the dashboard, click **Issue New Asset** (or navigate to **Assets** → **Issue New Asset**)
2. Fill in the form:
   - **Ticker** — Short symbol, 2-8 characters (e.g. "USDT", "TOKEN")
   - **Name** — Full name of the asset (e.g. "My Stablecoin")
   - **Amount** — Total supply to issue
   - **Precision** — Decimal places (0 = integer tokens, 2 = cents, 8 = like satoshis)
3. Click **Issue Asset**

The new asset will appear on your Assets page and in the dashboard.

### Step 5: Configure Payment Settings

1. Navigate to **Settings** (gear icon on the dashboard, or sidebar)
2. Under **Payment Configuration**:
   - **Accepted Asset** — Select the RGB asset customers must use for payment. RGB invoices will only accept this asset.
3. Under **UTXO Settings** (optional):
   - **UTXO Count** — How many colorable UTXOs to create at once (default: 4)
   - **UTXO Size** — Size of each UTXO in satoshis (default: 1000)
   - **Max Allocations per UTXO** — How many RGB allocations per UTXO (default: 10)
4. Under **Settlement**:
   - **Min Confirmations** — Number of blockchain confirmations required before marking a payment as settled (default: 1)
5. Click **Save Payment Settings**

### Step 6: Accept Payments

Once configured, RGB will appear as a payment method on your invoices:

1. Create an invoice in BTCPay (via UI or API)
2. On the checkout page, the customer will see an **RGB** payment option
3. The customer copies the RGB invoice string (starts with `rgb:`)
4. The customer pays using any RGB-compatible wallet (e.g. Iris Wallet, BitMask)
5. The invoice will transition through these states:
   - **New** — Waiting for payment
   - **Processing** — Payment detected, waiting for blockchain confirmations
   - **Settled** — Payment fully confirmed

### Monitoring

- **Dashboard** — Overview of BTC balance, colored balance, UTXO count, and asset list
- **Transfers** — View all incoming and outgoing RGB transfers with status (Pending → Settled)
- **BTC Transactions** — View on-chain Bitcoin transactions (UTXO creation, RGB sends, etc.)
- **UTXOs** — See which UTXOs hold RGB allocations and which are available

### Deleting a Wallet

1. Go to **Settings** → scroll to **Danger Zone**
2. Click **Delete Wallet**
3. Confirm the action

This removes the wallet from BTCPay (DB records, assets, invoices) but leaves the wallet data directory on disk for backup purposes.

## Invoice Settlement Flow

The plugin follows BTCPay's native two-step payment lifecycle:

```
Customer pays
       │
       ▼
RGB transfer detected (status: WaitingConfirmations)
       │
       ▼
BTCPay invoice → Processing
       │
       ▼ (blockchain confirmations reach threshold)
       │
RGB transfer confirmed (status: Settled)
       │
       ▼
BTCPay invoice → Settled
```

The plugin polls for transfer updates every 10 seconds. The number of confirmations required is configurable in Settings (default: 1).

## Building from Source

### Prerequisites

- .NET 10.0 SDK
- BTCPay Server source (as submodule)

### Build

```bash
git clone --recursive https://github.com/UTEXO-Protocol/rgb-btcpay-plugin
cd rgb-btcpay-plugin
dotnet build
```

### Plugin Builder Deployment

This plugin is designed for the [BTCPay Plugin Builder](https://github.com/btcpayserver/btcpayserver-plugin-builder):

1. Fork this repository
2. Register at https://plugin-builder.btcpayserver.org
3. Add your repository
4. Plugin Builder will build and publish automatically

## Architecture

```
BTCPayServer.Plugins.RgbUtexo/
├── Controllers/          # MVC controllers
├── Data/                 # EF Core entities & migrations
├── Models/               # View models
├── PaymentHandler/       # BTCPay payment integration
├── Services/
│   ├── RgbLibService.cs       # rgb-lib P/Invoke wrapper
│   ├── RgbLibWalletHandle.cs  # Wallet lifecycle management
│   ├── RGBWalletService.cs    # Wallet business logic
│   ├── MemoryWalletSigner.cs  # Local PSBT signing (NBitcoin)
│   ├── RgbWalletSignerProvider.cs # Signer management
│   ├── MnemonicProtectionService.cs # Mnemonic encryption
│   └── RGBInvoiceListener.cs  # Payment detection & settlement
├── Views/                # Razor views
└── native/rgb-verify/    # Rust native trust core (rgbverifycffi):
                          #   independent invoice decode + consignment
                          #   commitment verification for the pre-sign gate
                          #   (does NOT link rgb-lib — separate trust domain)
```

## Security Model

This plugin implements a **server-side custodial hot-wallet**. The BTCPay server operator holds the keys.

- **Mnemonic storage:** BIP-39 seed phrases are generated server-side and stored in the BTCPay database, encrypted with ASP.NET DataProtection.
- **Signing:** All transaction signing happens in-process on the BTCPay server. Any user with `CanModifyStoreSettings` permission can trigger signing operations.
- **Key access:** The seed phrase can be viewed by store admins after password re-verification (rate-limited).

> **Important:** This is custodial software. If the server is compromised, wallet funds are at risk. For production deployments, ensure your BTCPay server is properly secured. Back up your DataProtection key ring — losing it means losing access to all encrypted mnemonics.

### RGB Send Intent Verification (pre-sign gate)

Before signing any RGB **send**, the plugin independently verifies — outside the in-process `rgb-lib`, in a separate native trust core (`rgbverifycffi`, which does not link `rgb-lib`) — that the transaction it is about to sign commits to *exactly* the intended transfer:

- the RGB invoice decodes to the expected contract/asset ID, recipient blinded seal, amount, and network (decoded independently, never via `rgb-lib`);
- the operator-approved asset and amount match the invoice;
- the consignment commits to that single contract and transfer — no decoy, hidden, or co-located commitment;
- change returns to a wallet-owned script.

In addition, the plugin applies local BTC-level policy checks: it rejects BTC outputs to unknown scripts above the configured policy limit, restricts wallet/change outputs to locally-derived scripts, enforces fee and output-count limits, rejects non-zero-value `OP_RETURN` outputs, and routes all signing through the in-process wallet signer.

The gate is **fail-closed**: any verification failure aborts the transfer (`rgb-lib` `FailTransfers`) and nothing is signed. Because the verification baseline is the independent native decoder — never `rgb-lib`'s own decode — a compromised or malicious `rgb-lib` cannot construct a PSBT that passes the checks while diverting the transfer. By design a verifier bug can only cause a false *reject* (a legitimate send is blocked), never a false *accept* (funds diverted).

This closes the earlier trust boundary where RGB transfer construction was trusted entirely to `rgb-lib`.

A non-custodial mode with external signer support (offline PSBT signing, hardware wallet integration) is planned for a future release.

### DataProtection Key Backup

The mnemonic encryption keys are stored in your BTCPay data directory (e.g., `~/.btcpayserver/Main/DataProtection/`). If these files are lost (disk failure, container recreation without persistent volume), all encrypted mnemonics become permanently unrecoverable. **Back up these files alongside your database.**

## Dependencies

- **RgbLib** v0.3.0-beta.30 - Native rgb-lib bindings
- **rgbverifycffi** - In-repo native trust core for pre-sign RGB intent verification (`native/rgb-verify`, does not link rgb-lib)
- **NBitcoin** - Bitcoin primitives and PSBT signing
- **Npgsql.EntityFrameworkCore.PostgreSQL** - Database persistence

## Troubleshooting

### "InsufficientAllocationSlots"
Create more colorable UTXOs: go to **RGB Wallet** → **UTXOs** → **Create UTXOs**.

### "InsufficientAssignments"
The wallet has the asset but no spendable balance on colorable UTXOs. Create new UTXOs and wait for confirmations.

### Invoice stays in "Processing"
The blockchain hasn't reached the required number of confirmations yet. Wait for more blocks to be mined, or reduce Min Confirmations in Settings.

### Invoice stays in "New" after payment
1. Check Electrum connection: **Settings** → **Test Connection**
2. Ensure blocks are being mined (relevant for regtest/testnet)
3. Click **Refresh** on the RGB Wallet dashboard to trigger a manual sync

### "RGB pre-sign verification library could not be loaded"

At startup the plugin checks that the pre-sign verification library (`rgbverifycffi`) loads and exports the symbols it needs. If it does not, the plugin logs one error naming your runtime identifier, the file name it expected, and every path it searched — then keeps running.

While that error is present, **all RGB asset sends are rejected**. This is by design: the send path refuses to sign without the independent verification described in [RGB Send Intent Verification (pre-sign gate)](#rgb-send-intent-verification-pre-sign-gate). **Receiving RGB assets and the rest of the plugin are unaffected.**

The message tells you which of four things happened:

| The error says | What it means |
|---|---|
| the library **is absent from this build** | no candidate file existed — a known packaging defect in the plugin distribution, not a problem with your server |
| a file **exists but could not be loaded** | the file is there but unusable: architecture mismatch, corruption, or incompatible system libraries (commonly a glibc floor newer than the host) |
| the library **loaded but is the wrong version** | it loaded and an expected symbol is missing — an ABI/version mismatch between the plugin and the native library |
| the **self-check failed** | the check itself raised an exception, which the message names |

In every case, please report it at https://github.com/UTEXO-Protocol/rgb-btcpay-plugin/issues and quote the whole message.

### Plugin not loading
Check BTCPay logs for errors:
```bash
docker logs btcpay
```

If the plugin was auto-disabled after a crash, delete the disable command:
```bash
rm ~/.btcpayserver/Plugins/commands
```
Then restart BTCPay Server.

### Connection errors
Verify your Electrum server is reachable. Check `RGB_ELECTRUM_URL` environment variable or `rgb.json` configuration.

## Platform Support

| Platform | Status |
|----------|--------|
| Linux x64 | Supported |
| macOS ARM64 (Apple Silicon) | Supported |
| macOS x64 (Intel) | Not supported (native library not included) |
| Windows | Not supported (native library not included) |

## Building from source

This repository uses NuGet lockfiles (`packages.lock.json`) for both the plugin and test projects to pin transitive dependencies at known-good versions.

- First-time clone / clean restore: `dotnet restore --use-lock-file`
- Standard build verification: `dotnet restore --locked-mode` — this fails the build if any resolved version drifts from the committed lockfile.
- After a BTCPay submodule update (or any change to the plugin's direct/transitive packages): `dotnet restore --force-evaluate` regenerates the lockfile to match the new graph. Commit the regenerated `packages.lock.json`.
- The lockfile pins *version strings* only. It does NOT verify NuGet author signatures or SLSA provenance — those are deferred release-process controls.

### Packaging the gate native (`RgbVerifyCffi`)

The pre-sign verification library (`native/rgb-verify`, built by `native/rgb-verify/build-native.sh`) can also be packed as a native-only NuGet package. Nothing references that package yet — this is the machinery that produces it.

```bash
# stage every shipped RID and pack (host RID natively, cross RIDs in containers)
scripts/pack-rgbverify.sh --require-all-rids --version 0.11.1-rc.10-native.1

# pack only what is already staged (what CI's assemble job does)
scripts/pack-rgbverify.sh --pack-only --require-all-rids --version 0.11.1-rc.10-native.1

# run the pack-pipeline checks: package layout, both RID guards, and the Debian load check
scripts/pack-rgbverify.sh --verify
```

`--stage` and `--pack-only` are independent switches, not modes; passing neither does both. The shipped RID set (`linux-x64`, `linux-arm64`, `osx-arm64`) is declared once, in the packaging project's `GateRid` items, and the pack refuses to produce a package missing the production RID.

The package lands in `local-nuget-feed/`, which is **not** a source in the committed `nuget.config` — a folder source that cannot exist in a fresh clone fails restore with `NU1301` for every consumer, and a local source ahead of nuget.org could shadow the published trust core. Supply it on the command line instead:

```bash
dotnet restore <project> \
  --source https://api.nuget.org/v3/index.json \
  --source "$PWD/local-nuget-feed" \
  --force-evaluate
```

The feed path must be **absolute**. Measured on this project graph: both `--source ./local-nuget-feed` and the bare relative form fail with `NU1101 Unable to find package RgbVerifyCffi` when run from the repo root, while the identical restore with an absolute path succeeds. Reproduced with a cold cache in both directions; the reason relative resolution misses the feed here was not established, so use an absolute path.

`--force-evaluate` is needed because Rust builds are not byte-reproducible: re-packing at a version already restored elsewhere otherwise fails with `NU1403`.

**glibc floor.** The canonical Linux natives are built in `rust:1-bookworm` (Debian 12), because a native linked against a newer glibc than the deployment target fails to `dlopen` there. `scripts/verify-native-loads-debian.sh` loads the packed `linux-x64` native inside a Debian 12 container and resolves all four exports, so a floor mistake surfaces at pack time rather than at a merchant's startup.

For releases, `.github/workflows/pack-native.yml` (manual dispatch) builds each RID on a runner of that architecture, checks the exports with a tool that can read that object format, and uploads the assembled `.nupkg` as an artifact. It deliberately does not tag or publish a release.

## License

MIT License - See LICENSE file

## Support

- GitHub Issues: [Create Issue](https://github.com/UTEXO-Protocol/rgb-btcpay-plugin/issues)
- BTCPay Server Community: https://chat.btcpayserver.org
