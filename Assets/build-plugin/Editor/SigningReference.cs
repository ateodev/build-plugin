using System;

namespace Ateo.Build
{
    /// <summary>
    /// Android signing, expressed as REFERENCES only - never the secret itself. The asset is safe to commit:
    /// it names the keystore path (in the repo) + alias, and the ENV VAR NAMES that will hold the passwords
    /// at build time. CI injects the actual passwords into those env vars (resolved agent-side from the
    /// build server's per-game secret store); local devs export them from a gitignored override.
    /// </summary>
    [Serializable]
    public struct AndroidSigning
    {
        /// <summary>Keystore path relative to the project/checkout root (e.g. "keystore/user.keystore").</summary>
        public string keystoreFile;

        /// <summary>Signing key alias.</summary>
        public string keyAlias;

        /// <summary>Name of the env var holding the keystore password (default "ANDROID_KEYSTORE_PASS").</summary>
        public string keystorePasswordEnv;

        /// <summary>Name of the env var holding the key-alias password (default "ANDROID_KEYALIAS_PASS";
        /// falls back to the keystore password when unset).</summary>
        public string keyAliasPasswordEnv;

        public string KeystorePasswordEnvOrDefault => string.IsNullOrEmpty(keystorePasswordEnv) ? "ANDROID_KEYSTORE_PASS" : keystorePasswordEnv;
        public string KeyAliasPasswordEnvOrDefault => string.IsNullOrEmpty(keyAliasPasswordEnv) ? "ANDROID_KEYALIAS_PASS" : keyAliasPasswordEnv;

        public bool IsConfigured => !string.IsNullOrEmpty(keystoreFile) && !string.IsNullOrEmpty(keyAlias);
    }
}
