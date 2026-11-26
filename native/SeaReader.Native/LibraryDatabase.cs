using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SeaReader.Native;

internal sealed class LibraryDatabase : IDisposable
{
    private const int Row = 100;
    private const int Done = 101;
    private readonly DatabaseHandle connection;

    internal LibraryDatabase(string path)
    {
        const int readWriteAndCreate = 0x00000002 | 0x00000004;
        int result = Sqlite.Open(path, out connection, readWriteAndCreate, IntPtr.Zero);
        try
        {
            Check(result);
            Check(Sqlite.BusyTimeout(connection, 5000));
            ExecuteScript("PRAGMA foreign_keys=ON;");
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal void ExecuteScript(string sql) =>
        Check(Sqlite.Exec(connection, sql, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));

    internal void Execute(string sql, params object[] arguments)
    {
        using var statement = Prepare(sql, arguments);
        Check(Sqlite.Step(statement), Done);
    }

    internal List<T> Read<T>(string sql, Func<LibraryRow, T> map, params object[] arguments)
    {
        using var statement = Prepare(sql, arguments);
        var result = new List<T>();
        var row = new LibraryRow(statement);
        int status;
        while ((status = Sqlite.Step(statement)) == Row)
            result.Add(map(row));
        Check(status, Done);
        return result;
    }

    private StatementHandle Prepare(string sql, object[] arguments)
    {
        int status = Sqlite.Prepare(connection, sql, -1, out var statement, IntPtr.Zero);
        try
        {
            Check(status);
            for (int index = 0; index < arguments.Length; index++)
            {
                int parameter = index + 1;
                status = arguments[index] switch
                {
                    string text => Sqlite.BindText(statement, parameter, text, -1, new IntPtr(-1)),
                    long number => Sqlite.BindInteger(statement, parameter, number),
                    _ => throw new ArgumentException("Unsupported SQLite parameter type.")
                };
                Check(status);
            }
            return statement;
        }
        catch
        {
            statement.Dispose();
            throw;
        }
    }

    private void Check(int status, int expected = 0)
    {
        if (status == expected) return;
        string? detail = connection.IsInvalid ? null : Marshal.PtrToStringUTF8(Sqlite.ErrorMessage(connection));
        throw new InvalidOperationException($"无法访问 SeaReader 书库（SQLite {status}）。{detail}");
    }

    public void Dispose() => connection.Dispose();
}

internal readonly struct LibraryRow(StatementHandle statement)
{
    internal string Text(int index) => Marshal.PtrToStringUTF8(Sqlite.ColumnText(statement, index),
        Sqlite.ColumnBytes(statement, index)) ?? "";
    internal long Integer(int index) => Sqlite.ColumnInteger(statement, index);
    internal long? NullableInteger(int index) => Sqlite.ColumnType(statement, index) == 5 ? null : Integer(index);
}

internal sealed class DatabaseHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    protected override bool ReleaseHandle() => Sqlite.Close(handle) == 0;
}

internal sealed class StatementHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    protected override bool ReleaseHandle()
    {
        Sqlite.Finalize(handle);
        return true;
    }
}

internal static class Sqlite
{
    private const string Library = "winsqlite3.dll";

    [DllImport(Library, EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out DatabaseHandle connection,
        int flags, IntPtr vfs);
    [DllImport(Library, EntryPoint = "sqlite3_close_v2", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Close(IntPtr connection);
    [DllImport(Library, EntryPoint = "sqlite3_busy_timeout", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int BusyTimeout(DatabaseHandle connection, int milliseconds);
    [DllImport(Library, EntryPoint = "sqlite3_exec", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Exec(DatabaseHandle connection, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
        IntPtr callback, IntPtr argument, IntPtr error);
    [DllImport(Library, EntryPoint = "sqlite3_errmsg", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ErrorMessage(DatabaseHandle connection);
    [DllImport(Library, EntryPoint = "sqlite3_prepare_v2", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Prepare(DatabaseHandle connection, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
        int bytes, out StatementHandle statement, IntPtr unused);
    [DllImport(Library, EntryPoint = "sqlite3_bind_text", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int BindText(StatementHandle statement, int index,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int bytes, IntPtr destructor);
    [DllImport(Library, EntryPoint = "sqlite3_bind_int64", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int BindInteger(StatementHandle statement, int index, long value);
    [DllImport(Library, EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Step(StatementHandle statement);
    [DllImport(Library, EntryPoint = "sqlite3_column_text", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ColumnText(StatementHandle statement, int index);
    [DllImport(Library, EntryPoint = "sqlite3_column_bytes", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ColumnBytes(StatementHandle statement, int index);
    [DllImport(Library, EntryPoint = "sqlite3_column_int64", CallingConvention = CallingConvention.Cdecl)]
    internal static extern long ColumnInteger(StatementHandle statement, int index);
    [DllImport(Library, EntryPoint = "sqlite3_column_type", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ColumnType(StatementHandle statement, int index);
    [DllImport(Library, EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Finalize(IntPtr statement);
}
