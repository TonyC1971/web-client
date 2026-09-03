using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClassicUO.Utility.Logging;
using Microsoft.Data.Sqlite;

namespace ClassicUO.Game.Managers
{
    public class FriendliesSQLManager : IDisposable
    {
        public static FriendliesSQLManager Instance
        {
            get
            {
                if (field == null)
                    field = new();
                return field;
            }
            private set => field = value;
        }

        private const string DB_FILE = "friendlies.db";
        private const int MAX_BACKUPS = 3;

        private readonly SemaphoreSlim _dbLock = new(1, 1);
        private readonly string _dataDir;
        private readonly string _dataPath;
        private readonly string _connectionString;
        private bool _disposed;

        public FriendliesSQLManager()
        {
            _dataDir = Path.Combine(CUOEnviroment.ExecutablePath, "Data");
            _dataPath = Path.Combine(_dataDir, DB_FILE);

            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _dataPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            }.ToString();

            InitializeAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        }

        private async Task InitializeAsync()
        {
            await _dbLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!Directory.Exists(_dataDir))
                {
                    Directory.CreateDirectory(_dataDir);
                }

                // Create/open database and initialize table
                await using SqliteConnection connection = new(_connectionString);
                await connection.OpenAsync().ConfigureAwait(false);

                await using SqliteCommand createTableCmd = connection.CreateCommand();
                createTableCmd.CommandText = """
                                             CREATE TABLE IF NOT EXISTS friendlies (
                                                 serial INTEGER PRIMARY KEY,
                                                 name TEXT NOT NULL
                                             )
                                             """;
                await createTableCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                // Create index for faster name lookups
                await using SqliteCommand createIndexCmd = connection.CreateCommand();
                createIndexCmd.CommandText = """
                                             CREATE INDEX IF NOT EXISTS idx_name
                                             ON friendlies(name)
                                             """;
                await createIndexCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error($@"Error initializing FriendliesSQLManager: {ex.Message}");
                throw;
            }
            finally
            {
                _dbLock.Release();
            }
        }

        /// <summary>
        /// Asynchronously adds a friendly to the database, inserting or replacing as needed.
        /// </summary>
        /// <param name="serial">The serial of the entity</param>
        /// <param name="name">The name of the entity</param>
        /// <returns>A task that represents the asynchronous operation</returns>
        /// <exception cref="ObjectDisposedException">Thrown if the manager has been disposed</exception>
        public async Task AddAsync(uint serial, string name)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(FriendliesSQLManager));

            await _dbLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await using SqliteConnection connection = new(_connectionString);
                await connection.OpenAsync().ConfigureAwait(false);

                await using SqliteCommand cmd = connection.CreateCommand();
                cmd.CommandText = """
                                  INSERT OR REPLACE INTO friendlies (serial, name)
                                  VALUES ($serial, $name)
                                  """;
                cmd.Parameters.AddWithValue("$serial", serial);
                cmd.Parameters.AddWithValue("$name", name ?? string.Empty);

                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error($@"Error adding friendly {serial} ('{name}'): {ex.Message}");
            }
            finally
            {
                _dbLock.Release();
            }
        }

        /// <summary>
        /// Asynchronously removes a friendly from the database by serial.
        /// </summary>
        /// <param name="serial">The serial of the entity to remove</param>
        /// <returns>A task that represents the asynchronous operation</returns>
        /// <exception cref="ObjectDisposedException">Thrown if the manager has been disposed</exception>
        public async Task RemoveAsync(uint serial)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(FriendliesSQLManager));

            await _dbLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await using SqliteConnection connection = new(_connectionString);
                await connection.OpenAsync().ConfigureAwait(false);

                await using SqliteCommand cmd = connection.CreateCommand();
                cmd.CommandText = """
                                  DELETE FROM friendlies
                                  WHERE serial = $serial
                                  """;
                cmd.Parameters.AddWithValue("$serial", serial);

                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error($@"Error removing friendly {serial}: {ex.Message}");
            }
            finally
            {
                _dbLock.Release();
            }
        }

        /// <summary>
        /// Asynchronously checks if a serial is in the friendlies database.
        /// </summary>
        /// <param name="serial">The serial to check</param>
        /// <returns>A task that represents the asynchronous operation, containing true if the serial exists, false otherwise</returns>
        /// <exception cref="ObjectDisposedException">Thrown if the manager has been disposed</exception>
        public async Task<bool> ContainsAsync(uint serial)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(FriendliesSQLManager));

            await _dbLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await using SqliteConnection connection = new(_connectionString);
                await connection.OpenAsync().ConfigureAwait(false);

                await using SqliteCommand cmd = connection.CreateCommand();
                cmd.CommandText = """
                                  SELECT COUNT(*) FROM friendlies
                                  WHERE serial = $serial
                                  """;
                cmd.Parameters.AddWithValue("$serial", serial);

                long count = (long)await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                return count > 0;
            }
            catch (Exception ex)
            {
                Log.Error($@"Error checking friendly {serial}: {ex.Message}");
                return false;
            }
            finally
            {
                _dbLock.Release();
            }
        }

        /// <summary>
        /// Asynchronously retrieves a friendly's name by serial.
        /// </summary>
        /// <param name="serial">The serial to look up</param>
        /// <returns>A task that represents the asynchronous operation, containing the name or null if not found</returns>
        /// <exception cref="ObjectDisposedException">Thrown if the manager has been disposed</exception>
        public async Task<string> GetNameAsync(uint serial)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(FriendliesSQLManager));

            await _dbLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await using SqliteConnection connection = new(_connectionString);
                await connection.OpenAsync().ConfigureAwait(false);

                await using SqliteCommand cmd = connection.CreateCommand();
                cmd.CommandText = """
                                  SELECT name FROM friendlies
                                  WHERE serial = $serial
                                  """;
                cmd.Parameters.AddWithValue("$serial", serial);

                object result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                return result?.ToString();
            }
            catch (Exception ex)
            {
                Log.Error($@"Error getting name for friendly {serial}: {ex.Message}");
                return null;
            }
            finally
            {
                _dbLock.Release();
            }
        }

        /// <summary>
        /// Asynchronously retrieves all friendlies from the database.
        /// </summary>
        /// <returns>A task that represents the asynchronous operation, containing a dictionary mapping serials to names</returns>
        /// <exception cref="ObjectDisposedException">Thrown if the manager has been disposed</exception>
        public async Task<Dictionary<uint, string>> GetAllAsync()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(FriendliesSQLManager));

            Dictionary<uint, string> result = new();

            await _dbLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await using SqliteConnection connection = new(_connectionString);
                await connection.OpenAsync().ConfigureAwait(false);

                await using SqliteCommand cmd = connection.CreateCommand();
                cmd.CommandText = """
                                  SELECT serial, name FROM friendlies
                                  """;

                await using SqliteDataReader reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    uint serial = (uint)(long)reader.GetValue(0);
                    string name = reader.GetString(1);
                    result[serial] = name;
                }

                return result;
            }
            catch (Exception ex)
            {
                Log.Error($@"Error getting all friendlies: {ex.Message}");
                return result;
            }
            finally
            {
                _dbLock.Release();
            }
        }

        /// <summary>
        /// Asynchronously clears all friendlies from the database.
        /// </summary>
        /// <returns>A task that represents the asynchronous operation</returns>
        /// <exception cref="ObjectDisposedException">Thrown if the manager has been disposed</exception>
        public async Task ClearAsync()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(FriendliesSQLManager));

            await _dbLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await using SqliteConnection connection = new(_connectionString);
                await connection.OpenAsync().ConfigureAwait(false);

                await using SqliteCommand cmd = connection.CreateCommand();
                cmd.CommandText = """
                                  DELETE FROM friendlies
                                  """;

                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error($@"Error clearing friendlies: {ex.Message}");
            }
            finally
            {
                _dbLock.Release();
            }
        }

        /// <summary>
        /// Releases resources used by the FriendliesSQLManager.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _dbLock.Wait();
            try
            {
                _disposed = true;
            }
            finally
            {
                _dbLock.Release();
                _dbLock.Dispose();
            }

            Instance = null;
        }
    }
}
