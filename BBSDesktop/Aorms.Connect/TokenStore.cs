// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

namespace Aorms.Connect;

/// <summary>
/// Where the refresh token and session id live between runs. The app supplies a Windows Credential Manager implementation;
/// the library never writes secrets to disk itself.
/// </summary>
public interface IConnectTokenStore
{
    StoredSession? Load();
    void Save(StoredSession session);
    void Clear();
}

/// <summary>For tests and for runs that must not persist (e.g. the smoke tool).</summary>
public sealed class InMemoryTokenStore : IConnectTokenStore
{
    private StoredSession? _s;
    public StoredSession? Load() => _s;
    public void Save(StoredSession session) => _s = session;
    public void Clear() => _s = null;
}

/// <summary>
/// The sign-in shared by every AORMS suite app on one machine (AAD, AQC): one OS credential, target <c>AORMS/Connect</c>,
/// blob <c>{ "v":1, "refreshToken", "email", "updatedAt" }</c>. The per-app <see cref="IConnectTokenStore"/> keeps only that app's
/// session id. See docs/esti/AORMS-SUITE-SIGNIN.md in the AORMS repo.
/// </summary>
public sealed record SharedSignIn(string RefreshToken, string? Email, DateTimeOffset UpdatedAt);

public interface ISharedSignInStore
{
    SharedSignIn? Load();
    void Save(SharedSignIn signIn);
    void Clear();
}

public sealed class InMemorySharedSignInStore : ISharedSignInStore
{
    private SharedSignIn? _s;
    public SharedSignIn? Load() => _s;
    public void Save(SharedSignIn signIn) => _s = signIn;
    public void Clear() => _s = null;
}
