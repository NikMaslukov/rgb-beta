const path = require('path');
const fs = require('fs');

const SDK_PATHS = [
    process.env.RGB_SDK_PATH,
    path.resolve(__dirname, 'rgb-sdk', 'index.cjs'),
    path.resolve(__dirname, '../rgb-sdk/dist/index.cjs'),
    path.resolve(__dirname, '../../rgb-sdk/dist/index.cjs')
].filter(Boolean);

let sdk = null, walletManager = null, walletConfig = null;

function ensureSdk() {
    if (sdk) return sdk;
    for (const p of SDK_PATHS) {
        try {
            if (fs.existsSync(p)) {
                sdk = require(p);
                return sdk;
            }
        } catch (e) { /* keep trying */ }
    }
    throw new Error('cant find rgb-sdk');
}

module.exports.initWallet = function(callback, cfg) {
    try {
        if (!cfg || (!cfg.XpubVanilla && !cfg.xpubVanilla)) {
            return callback(null, { success: false, error: 'need xpub' });
        }
        ensureSdk();
        var rgbNodeEndpoint = cfg.RgbNodeEndpoint || cfg.rgbNodeEndpoint;
        var network = cfg.Network || cfg.network;
        if (!rgbNodeEndpoint) {
            return callback(null, { success: false, error: 'RGB node endpoint is required' });
        }
        if (!network) {
            return callback(null, { success: false, error: 'Network is required' });
        }
        walletConfig = {
            xpub_van: cfg.XpubVanilla || cfg.xpubVanilla,
            xpub_col: cfg.XpubColored || cfg.xpubColored,
            mnemonic: cfg.Mnemonic || cfg.mnemonic,
            master_fingerprint: cfg.MasterFingerprint || cfg.masterFingerprint,
            rgb_node_endpoint: rgbNodeEndpoint,
            network: network
        };
        walletManager = new sdk.WalletManager(walletConfig);
        walletManager.registerWallet()
            .then(() => callback(null, { success: true }))
            .catch(e => callback(null, { success: false, error: e.message }));
    } catch (e) {
        callback(null, { success: false, error: e.message });
    }
};

/**
 * Get wallet status
 */
module.exports.getStatus = function(callback) {
    try {
        const status = {
            SdkLoaded: sdk !== null,
            WalletInitialized: walletManager !== null,
            HasMnemonic: walletConfig?.mnemonic ? true : false,
            Connected: walletManager !== null
        };
        
        if (walletManager) {
            walletManager.getBtcBalance()
                .then(balance => {
                    status.BtcBalance = balance;
                    callback(null, status);
                })
                .catch(() => {
                    callback(null, status);
                });
        } else {
            callback(null, status);
        }
    } catch (e) {
        callback(null, { 
            SdkLoaded: false, 
            WalletInitialized: false,
            HasMnemonic: false,
            Connected: false,
            Error: e.message 
        });
    }
};

/**
 * Sign a PSBT
 */
module.exports.signPsbt = function(callback, psbtBase64) {
    if (!walletManager) {
        return callback(null, { success: false, error: 'Wallet not initialized' });
    }
    
    walletManager.signPsbt(psbtBase64)
        .then(signedPsbt => {
            callback(null, { success: true, SignedPsbt: signedPsbt });
        })
        .catch(err => {
            callback(null, { success: false, error: err.message });
        });
};

/**
 * Create colorable UTXOs
 */
module.exports.createUtxos = function(callback, num, size) {
    if (!walletManager) {
        return callback(null, { success: false, error: 'Wallet not initialized' });
    }
    
    walletManager.createUtxos({ up_to: true, num: num || 5, size: size || 10000, fee_rate: 2 })
        .then(result => {
            callback(null, { success: true, UtxosCreated: result });
        })
        .catch(err => {
            // AllocationsAlreadyAvailable is not an error
            if (err.message && err.message.includes('AllocationsAlreadyAvailable')) {
                callback(null, { success: true, UtxosCreated: 0 });
            } else {
                callback(null, { success: false, error: err.message });
            }
        });
};

/**
 * Issue a new RGB asset
 */
module.exports.issueAssetNia = function(callback, ticker, name, amounts, precision) {
    if (!walletManager) {
        return callback(null, { success: false, error: 'Wallet not initialized' });
    }
    
    walletManager.issueAssetNia({ ticker, name, amounts, precision: precision || 0 })
        .then(result => {
            callback(null, { 
                success: true, 
                AssetId: result.asset?.asset_id 
            });
        })
        .catch(err => {
            callback(null, { success: false, error: err.message });
        });
};

/**
 * Send BTC
 */
module.exports.sendBtc = function(callback, address, amount, feeRate) {
    if (!walletManager) {
        return callback(null, { success: false, error: 'Wallet not initialized' });
    }
    
    walletManager.sendBtc({ address, amount, fee_rate: feeRate || 2 })
        .then(txid => {
            callback(null, { success: true, Txid: txid });
        })
        .catch(err => {
            callback(null, { success: false, error: err.message });
        });
};

/**
 * Send RGB assets
 */
module.exports.sendRgb = function(callback, invoice, amount, assetId) {
    if (!walletManager) {
        return callback(null, { success: false, error: 'Wallet not initialized' });
    }
    
    walletManager.send({ invoice, amount, asset_id: assetId })
        .then(result => {
            callback(null, { success: true, Txid: result.txid });
        })
        .catch(err => {
            callback(null, { success: false, error: err.message });
        });
};

/**
 * Dispose wallet
 */
module.exports.disposeWallet = function(callback) {
    if (walletManager) {
        try {
            walletManager.dispose();
        } catch (e) {
            // Ignore dispose errors
        }
        walletManager = null;
    }
    walletConfig = null;
    callback(null, { success: true, status: 'disposed' });
};

