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
		#region Fields

		/// <summary>Keystore path relative to the project/checkout root (e.g. "keystore/user.keystore").</summary>
		public string KeystoreFile;

		/// <summary>Signing key alias.</summary>
		public string KeyAlias;

		/// <summary>Name of the env var holding the keystore password (default "ANDROID_KEYSTORE_PASS").
		/// Doubles as the LOGICAL registry key the value is resolved by (agent-side and locally).</summary>
		[SecretKeyField(SecretKind.String)]
		public string KeystorePasswordEnv;

		/// <summary>Name of the env var holding the key-alias password (default "ANDROID_KEYALIAS_PASS";
		/// falls back to the keystore password when unset). Doubles as the logical registry key.</summary>
		[SecretKeyField(SecretKind.String)]
		public string KeyAliasPasswordEnv;

		#endregion

		#region Constructor

		public AndroidSigning(string keystoreFile, string keyAlias, string keystorePasswordEnv, string keyAliasPasswordEnv)
		{
			KeystoreFile = keystoreFile;
			KeyAlias = keyAlias;
			KeystorePasswordEnv = keystorePasswordEnv;
			KeyAliasPasswordEnv = keyAliasPasswordEnv;
		}

		#endregion

		#region Properties

		public string KeystorePasswordEnvOrDefault => string.IsNullOrEmpty(KeystorePasswordEnv) ? "ANDROID_KEYSTORE_PASS" : KeystorePasswordEnv;
		public string KeyAliasPasswordEnvOrDefault => string.IsNullOrEmpty(KeyAliasPasswordEnv) ? "ANDROID_KEYALIAS_PASS" : KeyAliasPasswordEnv;
		public bool IsConfigured => !string.IsNullOrEmpty(KeystoreFile) && !string.IsNullOrEmpty(KeyAlias);

		#endregion
	}

	/// <summary>
	/// iOS signing, expressed as REFERENCES only - never the secret itself (mirrors <see cref="AndroidSigning"/>).
	/// The asset is safe to commit: it names the Apple Developer team + provisioning profile and the ENV VAR
	/// NAMES that will hold the fastlane match passphrase + App Store Connect API key at build time. CI injects
	/// the actual values into those env vars (resolved agent-side from the build server's per-game secret store);
	/// local devs export them from a gitignored override. Unity's iOS build produces an Xcode project; the actual
	/// codesign/match step runs in the post-build pipeline (see the BuildIPA action), which reads these references.
	/// </summary>
	[Serializable]
	public struct iOSSigning
	{
		#region Fields

		/// <summary>Apple Developer Team ID (e.g. "ABCDE12345").</summary>
		public string AppleTeamId;

		/// <summary>Provisioning profile name / specifier (e.g. "match AppStore com.ateo.game").</summary>
		public string ProvisioningProfile;

		/// <summary>Name of the env var holding the fastlane match passphrase (default "MATCH_PASSWORD").
		/// Doubles as the LOGICAL registry key the value is resolved by (agent-side and locally).</summary>
		[SecretKeyField(SecretKind.String)]
		public string MatchPasswordEnv;

		/// <summary>Name of the env var holding the App Store Connect API key reference (default "ASC_API_KEY").
		/// Doubles as the logical registry key.</summary>
		[SecretKeyField(SecretKind.File)]
		public string AscApiKeyEnv;

		#endregion

		#region Constructor

		public iOSSigning(string appleTeamId, string provisioningProfile, string matchPasswordEnv, string ascApiKeyEnv)
		{
			AppleTeamId = appleTeamId;
			ProvisioningProfile = provisioningProfile;
			MatchPasswordEnv = matchPasswordEnv;
			AscApiKeyEnv = ascApiKeyEnv;
		}

		#endregion

		#region Properties

		public string MatchPasswordEnvOrDefault => string.IsNullOrEmpty(MatchPasswordEnv) ? "MATCH_PASSWORD" : MatchPasswordEnv;
		public string AscApiKeyEnvOrDefault => string.IsNullOrEmpty(AscApiKeyEnv) ? "ASC_API_KEY" : AscApiKeyEnv;
		public bool IsConfigured => !string.IsNullOrEmpty(AppleTeamId) && !string.IsNullOrEmpty(ProvisioningProfile);

		#endregion
	}
}
