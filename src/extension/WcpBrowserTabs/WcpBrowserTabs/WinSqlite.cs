// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WcpBrowserTabs;

/// <summary>
/// Minimal wrapper over the SQLite engine that ships with Windows (winsqlite3.dll).
/// </summary>
internal sealed partial class WinSqliteConnection : IDisposable
{
    private const int SqliteOk = 0;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;
    private static readonly nint SqliteTransient = -1;

    private nint handle;

    private WinSqliteConnection(nint handle)
    {
        this.handle = handle;
    }

    public static WinSqliteConnection Open(string path)
    {
        var result = NativeMethods.sqlite3_open16(path, out var db);
        if (result != SqliteOk)
        {
            if (db != nint.Zero)
            {
                _ = NativeMethods.sqlite3_close(db);
            }

            throw new InvalidOperationException($"Unable to open SQLite database (error {result}).");
        }

        _ = NativeMethods.sqlite3_busy_timeout(db, 250);
        return new WinSqliteConnection(db);
    }

    public void Execute(string sql, params string[] parameters)
    {
        Query(sql, parameters, _ => { });
    }

    public void Query(string sql, IReadOnlyList<string> parameters, Action<WinSqliteRow> onRow)
    {
        ObjectDisposedException.ThrowIf(handle == nint.Zero, this);
        ArgumentNullException.ThrowIfNull(onRow);

        var result = NativeMethods.sqlite3_prepare16_v2(handle, sql, -1, out var statement, nint.Zero);
        if (result != SqliteOk)
        {
            throw new InvalidOperationException($"Unable to prepare SQLite statement (error {result}).");
        }

        try
        {
            for (var i = 0; i < parameters.Count; i++)
            {
                result = NativeMethods.sqlite3_bind_text16(statement, i + 1, parameters[i], -1, SqliteTransient);
                if (result != SqliteOk)
                {
                    throw new InvalidOperationException($"Unable to bind SQLite parameter (error {result}).");
                }
            }

            var row = new WinSqliteRow(statement);
            while (true)
            {
                result = NativeMethods.sqlite3_step(statement);
                if (result == SqliteRow)
                {
                    onRow(row);
                    continue;
                }

                if (result == SqliteDone)
                {
                    return;
                }

                throw new InvalidOperationException($"SQLite query failed (error {result}).");
            }
        }
        finally
        {
            _ = NativeMethods.sqlite3_finalize(statement);
        }
    }

    public void Dispose()
    {
        if (handle != nint.Zero)
        {
            _ = NativeMethods.sqlite3_close(handle);
            handle = nint.Zero;
        }
    }

    internal readonly struct WinSqliteRow
    {
        private readonly nint statement;

        internal WinSqliteRow(nint statement)
        {
            this.statement = statement;
        }

        public string? GetString(int column)
        {
            var text = NativeMethods.sqlite3_column_text16(statement, column);
            return text == nint.Zero ? null : Marshal.PtrToStringUni(text);
        }

        public byte[]? GetBlob(int column)
        {
            var blob = NativeMethods.sqlite3_column_blob(statement, column);
            var length = NativeMethods.sqlite3_column_bytes(statement, column);
            if (blob == nint.Zero || length <= 0)
            {
                return null;
            }

            var bytes = new byte[length];
            Marshal.Copy(blob, bytes, 0, length);
            return bytes;
        }
    }

    private static class NativeMethods
    {
        private const string Library = "winsqlite3.dll";

        [DllImport(Library, CharSet = CharSet.Unicode)]
        internal static extern int sqlite3_open16(string filename, out nint db);

        [DllImport(Library)]
        internal static extern int sqlite3_close(nint db);

        [DllImport(Library)]
        internal static extern int sqlite3_busy_timeout(nint db, int milliseconds);

        [DllImport(Library, CharSet = CharSet.Unicode)]
        internal static extern int sqlite3_prepare16_v2(nint db, string sql, int numBytes, out nint statement, nint tail);

        [DllImport(Library, CharSet = CharSet.Unicode)]
        internal static extern int sqlite3_bind_text16(nint statement, int index, string value, int numBytes, nint destructor);

        [DllImport(Library)]
        internal static extern int sqlite3_step(nint statement);

        [DllImport(Library)]
        internal static extern int sqlite3_finalize(nint statement);

        [DllImport(Library)]
        internal static extern nint sqlite3_column_text16(nint statement, int column);

        [DllImport(Library)]
        internal static extern nint sqlite3_column_blob(nint statement, int column);

        [DllImport(Library)]
        internal static extern int sqlite3_column_bytes(nint statement, int column);
    }
}
