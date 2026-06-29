namespace Ateo.Build
{
	/// <summary>
	/// Which side is resolving a secret, passed to <see cref="ISecretProvider.ResolveAsync"/>. <see cref="Local"/>
	/// = the dev's open Editor (uses the dev's own provider session, e.g. the 1Password desktop app, offline);
	/// <see cref="Server"/> = the headless CI agent (uses the environment-local robot bootstrap). Lets a provider
	/// pick the right auth path without the caller hardwiring it. See build-plugin-architecture.md §11.1.
	/// </summary>
	public enum ExecContext
	{
		Local,
		Server
	}
}
