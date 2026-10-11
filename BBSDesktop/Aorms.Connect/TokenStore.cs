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
