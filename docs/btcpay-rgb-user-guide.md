# Comprehensive User Guide: BTCPay Server RGB Plugin

Here you can find the user guide for the **Utexo BTCPay Plugin**. This documentation is specifically designed to guide beginners through setting up their stores, understanding underlying blockchain mechanics, issuing custom assets and executing secure peer-to-peer transfers.

---

# 1. What is RGB?

To successfully manage assets using the BTCPay Server plugin, it is important to understand the structural paradigm shift RGB introduces to smart contracts: **Client-Side Validation** and **Bitcoin's role as a consensus anchor**.

### Client-Side Validation (CSV)
Traditional blockchain networks (like Ethereum) operate on a global consensus model where every single node downloads, validates and stores every transaction and smart contract execution. This severely limits privacy and network scalability.

**Client-Side Validation (CSV)** fundamentally redefines this model:
* **Data Privacy:** Smart contract data, asset histories and state transitions are kept completely off-chain. They are only shared directly peer-to-peer (P2P) between the sender and the receiver.
* **Independent Auditing:** The receiver independently inspects and validates the entire cryptographic proof history of the asset being sent to them. The rest of the network remains unaware that a transaction even occurred.

### Bitcoin's Role: The Ultimate Timestamp Server
Since RGB asset data lives off-chain, how do we prevent someone from sending the same asset to two different people (Double-Spending)? 

RGB solves this by using Bitcoin as a **Consensus Layer and Decentralized Timestamp Server**. It maps off-chain smart contract states to standard Bitcoin **UTXOs (Unspent Transaction Outputs)** using a cryptographic mechanism called a **Single-Use Seal**:
1. An RGB asset is cryptographically bound ("anchored") to a specific, unspent Bitcoin transaction output.
2. To transfer ownership of that RGB asset, the underlying Bitcoin UTXO must be spent on the Bitcoin blockchain.
3. Once that Bitcoin UTXO is spent, the single-use seal is broken forever. The network prevents double-spending because the Bitcoin blockchain guarantees that a UTXO can only be spent exactly once.

---

# 2. What is the BTCPay RGB Plugin?

The BTCPay Plugin bridges the gap between intricate command-line RGB protocol interactions and business operations. It wraps complex cryptographic tasks (eg. proof generation, asset validation and state anchoring) into an intuitive user friendly interface.

### Key Capabilities
* **Custom Asset Issuance:** Mint digital tokens, collectibles or localized currencies directly within your store interface.
* **Unified Financial Dashboard:** Monitor both base-layer Bitcoin balances and custom RGB token balances seamlessly.
* **Automated UTXO Processing:** Let the plugin handle the technical isolation of transaction outputs required to secure your tokens.

> ***Note:***
>  When configuring a new store, clicking the first, default **"Setup a Wallet"** prompt will create a standard Bitcoin wallet. To unlock RGB features, you must specifically select the dedicated **RGB Wallet** on the wallets section.

> ***Warning:***
> **Network Selection Constraint**
> The user interface, when creating a new RGB Wallet, presents a dropdown menu offering choice across all standard environment configurations: **Mainnet, Signet, Testnet, Regtest and Utexo**. 
> 
> However, **you cannot choose freely**. You are strictly limited to selecting the exact network that the underlying BTCPay Server node is currently actively configured for. For example, if the host instance is running on a Signet backend, generating a wallet set to 'Mainnet' or 'Testnet' will result in an initialization failure. Always verify your server's base operational layer before selection.

> ***Info:***
> **Network Naming Convention:** Selecting **"Testnet"** within the plugin environment specifically maps to the historical and standard **Testnet3** infrastructure.

---

# 3. Frequently Asked Questions (FAQ)

### Can I still process standard Bitcoin transactions?
Absolutely. The RGB Wallet preserves all fundamental base-layer Bitcoin features. Sending and receiving normal BTC utilizes standard payment rails, meaning you don't lose any default store capabilities by upgrading your interface to handle RGB.

### The "First-Time Asset Receiving" 
A major UX roadblock occurs when a store attempts to generate a dedicated invoice for a newly created asset that their wallet doesn't yet hold.

**The Problem:** Because RGB operates on Client-Side Validation, your wallet is completely blind to an asset's existence until it physically possesses a piece of that asset's validated history. If you go to `Invoices -> Create Invoice` you can't request a token that your wallet doesn't recognize.

**The Step-by-Step Workaround:**
To receive an asset that your wallet has never previously held, you must bypass the standard invoicing funnel entirely and use the Universal Asset Ingestion workflow:

1. Navigate to the **RGB Wallet**.
2. Click the **"Receive any asset"** button. This creates a special blind invoice capable of receiving any valid incoming contract schema.
3. Provide this universal invoice to the sender so they can transmit the initial allocation of the asset.
4. Once the transaction clears and your wallet processes the off-chain validation proofs, navigate directly to your **RGB Wallet Settings**.
5. Set the **Accepted Asset** in the settings of the wallet in order to receive that specific asset.
6. **Result:** Your store now fully recognizes it. You can now use the standard `Create Invoice` system to routinely request and accept this asset from regular customers.

---

# 4. Quickstart: End-to-End Example Asset Lifecycle

Below is a complete, hands-on demonstration of configuring two distinct store environments, creating an asset, sending and receiving BTC and Assets.

### Visual Walkthrough
Here is a demo of the steps described after:

[![BTCPay Server RGB Plugin Guide](https://img.youtube.com/vi/qGPV-d6gDHA/0.jpg)](https://www.youtube.com/watch?v=qGPV-d6gDHA)

---

## Phase A: Setup and Asset Issuance ("TEST 1" Store)

#### Step 1: Initialize the "TEST 1" RGB Wallet
Navigate to your primary store settings dashboard, select the **RGB Wallet** configuration menu and create it. Be certain to explicitly match your server's underlying network parameters (e.g., Utexo Custom Signet).

#### Step 2: Fund the On-Chain Base Layer
Copy your newly generated Bitcoin deposit address. Access a compatible network faucet to acquire testing funds. If you are operating on the Utexo custom signet environment, use the automated Telegram bot: [Utexo RLN bot](https://t.me/Utexo_RLN_bot).
* *Why this is necessary:* Even though RGB assets operate on a virtual overlay, every lifecycle movement (issuance, allocation and transferring) requires standard Bitcoin miner fees to commit transactions to the base ledger. 

#### Step 3: Create Colorable UTXOs
Navigate to **Manage UTXOs ➔ Create UTXOs**. 

* *Why this is necessary:* a **Colorable UTXO** is a standard Bitcoin Unspent Transaction Output that is specifically structured, sized and flagged by your wallet software to hold an RGB smart contract state. If you attempt to issue tokens without provisioning these special outputs first, the contract data will have no physical "container" to bind to on the blockchain and issuance will fail.

#### Step 4: Issue the Asset
With colorable UTXOs active, click **Issue Asset**. Input your parameters (e.g., Asset Ticker: `TSTOKEN`, Amount: `1000`). Confirm the deployment.
> **Warning:** Issuing an asset requires at least one colorable UTXO. Make sure you have funded your wallet and created UTXOs before issuing. ([Step 2](#step-2-fund-the-on-chain-base-layer) and [Step 3](#step-3-create-colorable-utxos))

---

## Phase B: Store-to-Store Transfer & Invoicing ("TEST 2" Store)

#### Step 1: Create the "TEST 2" Receiving Store
Set up an entirely separate store environment within your BTCPay instance named **"TEST 2"**. Initialize its **RGB Wallet** following the exact same network rules applied in Phase A.
* *Why this is necessary:* This establishes a clean, sandboxed secondary entity to accurately test real-world P2P transactions.

#### Step 2: Send BTC to "TEST 2"
Copy the address belonging to the **RGB Wallet** of **TEST 2**. Switch back to your **TEST 1** store dashboard, click **Send BTC** in the **RGB Wallet**, paste the address and execute a small transfer of standard Bitcoin.
* *Why this is necessary:* An RGB wallet with zero on-chain Bitcoin cannot execute state transitions.

#### Step 3: Create Colorable UTXOs in "TEST 2"
While logged inside the **TEST 2** store, go to **Manage UTXOs ➔ Create UTXOs** inside the **RGB Wallet**.
* *Why this is necessary:* Just like issuance, *receiving* an asset requires a dedicated, empty physical container on the blockchain. **TEST 2** must hold a clean, unencumbered colorable UTXO so that the incoming asset state transition can anchor safely to its keys.

#### Step 4: Execute the Universal Asset Ingestion
Because **TEST 2** has a balance of zero and does not recognize `TSTOKEN`, you cannot use a standard invoice yet. Inside **TEST 2**, click **Receive any asset** and copy the specialized string provided.
* *Why this is necessary:* This acts as a blind intake mechanism, allowing the store to listen for and accept any inbound asset structure without requiring pre-existing balance definitions.

#### Step 5: Sending the New Asset from "TEST 1"
Switch back to **TEST 1**. Go to your **RGB Wallet ➔ Send Asset**. Paste the universal invoice copied from **TEST 2**, input the amount of `TSTOKEN` you want to transfer, and click send.
* *Why this is necessary:* This triggers the core client-side validation logic. **TEST 1** takes the underlying Bitcoin output holding the tokens, constructs a valid spending transaction, attaches the cryptographic state evolution proof showing that ownership is moving to **TEST 2** and broadcasts the transaction to the Bitcoin network.

#### Step 6: Configure Asset Preference in "TEST 2"
Wait for the transaction to be confirmed. Once received inside **TEST 2**, open your **RGB Wallet ➔ Settings ➔ Accepted Assets** and select the validated `TSTOKEN` entry.
* *Why this is necessary:* This manually promotes the newly discovered asset into the primary application layer, allowing the internal billing engine to use it for invoice creation.

#### Step 7: Generate a Standard Invoice
Inside **TEST 2**, go to the core menu and select **Invoices ➔ Create Invoice**. Under the store selection options, select **RGB** now your newly enabled `TSTOKEN` asset will be automatically the one expected in the invoice.
* *Why this is necessary:* Now that the wallet actively holds and recognizes the token history, it can generate automated invoices specific to that asset.

#### Step 8: Execute Final Settlement Payment
Copy the newly generated specific invoice. Return to your **TEST 1** store **RGB Wallet** dashboard, execute a final transfer using the generated invoice to transfer specifically the `TSTOKEN`asset.
