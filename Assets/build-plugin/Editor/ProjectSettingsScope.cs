using System;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace Ateo.Build
{
	/// <summary>
	/// Snapshots the player/editor settings a build mutates (scripting defines, version, Android signing
	/// names, app-bundle toggle) and restores them on <see cref="Dispose"/>. Wrap a build in a
	/// <c>using</c> so the checkout stays pristine afterwards - important for the no-clean incremental
	/// checkout model (a build must not leave its target/flavor/version changes committed in
	/// ProjectSettings). Mirrors the try/finally restore in hand-rolled game builders.
	///
	/// Note: changes are made in-memory only (we never save the project), so disk stays clean unless Unity
	/// auto-saves on exit - and by then this scope has already restored the originals.
	/// </summary>
	public sealed class ProjectSettingsScope : IDisposable
	{
		#region Fields

		private readonly NamedBuildTarget _namedTarget;
		private readonly string[] _defines;
		private readonly bool _buildAppBundle;
		private readonly bool _useCustomKeystore;
		private readonly string _keystoreName;
		private readonly string _keyaliasName;
		private readonly string _bundleVersion;
		private readonly int _androidVersionCode;
		private readonly string _iosBuildNumber;

		#endregion

		#region Constructor

		public ProjectSettingsScope(BuildTargetGroup group)
		{
			_namedTarget = NamedBuildTarget.FromBuildTargetGroup(group);
			PlayerSettings.GetScriptingDefineSymbols(_namedTarget, out _defines);
			_buildAppBundle = EditorUserBuildSettings.buildAppBundle;
			_useCustomKeystore = PlayerSettings.Android.useCustomKeystore;
			_keystoreName = PlayerSettings.Android.keystoreName;
			_keyaliasName = PlayerSettings.Android.keyaliasName;
			_bundleVersion = PlayerSettings.bundleVersion;
			_androidVersionCode = PlayerSettings.Android.bundleVersionCode;
			_iosBuildNumber = PlayerSettings.iOS.buildNumber;
		}

		#endregion

		#region IDisposable

		public void Dispose()
		{
			PlayerSettings.SetScriptingDefineSymbols(_namedTarget, _defines);
			EditorUserBuildSettings.buildAppBundle = _buildAppBundle;
			PlayerSettings.Android.useCustomKeystore = _useCustomKeystore;
			PlayerSettings.Android.keystoreName = _keystoreName;
			PlayerSettings.Android.keyaliasName = _keyaliasName;
			PlayerSettings.bundleVersion = _bundleVersion;
			PlayerSettings.Android.bundleVersionCode = _androidVersionCode;
			PlayerSettings.iOS.buildNumber = _iosBuildNumber;
		}

		#endregion
	}
}
