using System;
using UnityEditor;
using UnityEngine;

namespace Ateo.Build
{
	/// <summary>
	/// Applies CI version overrides to PlayerSettings before a build, and records what was stamped on the
	/// context. Precedence for every value: explicit env override &gt; (build-counter env, supplied by CI) &gt;
	/// committed ProjectSettings. Reads the same env vars the build server's executor injects from the
	/// <c>unitybuild.version.*</c> params:
	///   - <c>BUILD_VERSION_NAME</c>  -&gt; PlayerSettings.bundleVersion   (marketing version)
	///   - <c>ANDROID_VERSION_CODE</c> -&gt; PlayerSettings.Android.bundleVersionCode
	///   - <c>IOS_BUILD_NUMBER</c>     -&gt; PlayerSettings.iOS.buildNumber
	/// (The version-name override closes the gap noted in the param contract: CIBuild previously had no env
	/// path for the marketing version. This package is the canonical place that honors it.)
	/// </summary>
	public static class VersionStamp
	{
		#region Public Methods

		public static void Apply(BuildContext context)
		{
			// Marketing version (all platforms): override from BUILD_VERSION_NAME, else keep ProjectSettings.
			string nameEnv = Environment.GetEnvironmentVariable("BUILD_VERSION_NAME");
			if (!string.IsNullOrEmpty(nameEnv))
			{
				PlayerSettings.bundleVersion = nameEnv.Trim();
				Debug.Log("[Build] versionName from BUILD_VERSION_NAME = " + nameEnv.Trim());
			}

			context.VersionName = PlayerSettings.bundleVersion;

			switch (context.Platform)
			{
				case BuildPlatform.Android:
				{
					int code = PlayerSettings.Android.bundleVersionCode;
					string codeEnv = Environment.GetEnvironmentVariable("ANDROID_VERSION_CODE");
					if (int.TryParse(codeEnv, out int parsed) && parsed > 0)
					{
						PlayerSettings.Android.bundleVersionCode = parsed;
						code = parsed;
						Debug.Log("[Build] versionCode from ANDROID_VERSION_CODE = " + parsed);
					}
					else
					{
						Debug.Log("[Build] versionCode from ProjectSettings = " + code + " (no env override)");
					}

					context.VersionCode = code;
					break;
				}
				case BuildPlatform.iOS:
				{
					string numEnv = Environment.GetEnvironmentVariable("IOS_BUILD_NUMBER");
					if (!string.IsNullOrEmpty(numEnv))
					{
						PlayerSettings.iOS.buildNumber = numEnv.Trim();
						Debug.Log("[Build] iOS buildNumber from IOS_BUILD_NUMBER = " + numEnv.Trim());
					}

					int.TryParse(PlayerSettings.iOS.buildNumber, out int parsedNumber);
					context.VersionCode = parsedNumber;
					break;
				}
			}
		}

		#endregion
	}
}
