
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.WindowsAzure;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;

using Wintellect.Sterling.Core;
using Wintellect.Sterling.Core.Database;
using Wintellect.Sterling.Core.Exceptions;
using Wintellect.Sterling.Core.Serialization;

namespace Wintellect.Sterling.Server.Azure.TableStorage
{
    public class Driver : BaseDriver
    {
        private Lazy<CloudTableClient> _client = null;
        private Lazy<CloudTable> _keysTable = null;
        private Lazy<CloudTable> _indexesTable = null;
        private Lazy<CloudTable> _typesTable = null;
        private bool _dirtyType;

        public Driver()
        {
            Initialize();
        }

        public Driver( string databaseName, ISterlingSerializer serializer, Action<SterlingLogLevel, string, Exception> log )
            : base( databaseName, serializer, log )
        {
            Initialize();
        }

        public string KeysTable { get; set; }
        public string IndexesTable { get; set; }
        public string TypesTable { get; set; }

        public string ConnectionString { get; set; }

        private void Initialize()
        {
            _client = new Lazy<CloudTableClient>( () =>
            {
                var account = CloudStorageAccount.Parse( ConnectionString ?? "UseDevelopmentStorage=true" /* local dev emulator */ );
                return account.CreateCloudTableClient();
            } );

            _keysTable = new Lazy<CloudTable>( () =>
            {
                var table = _client.Value.GetTableReference( KeysTable ?? "sterlingkeys" );
                table.CreateIfNotExists();
                return table;
            } );

            _indexesTable = new Lazy<CloudTable>( () =>
            {
                var table = _client.Value.GetTableReference( IndexesTable ?? "sterlingindexes" );
                table.CreateIfNotExists();
                return table;
            } );

            _typesTable = new Lazy<CloudTable>( () =>
            {
                var table = _client.Value.GetTableReference( TypesTable ?? "sterlingtypes" );
                table.CreateIfNotExists();
                return table;
            } );
        }

        private static async Task Store( CloudTable table, Action<BinaryWriter> action, string partitionKey, string rowKey )
        {
            using ( var stream = new MemoryStream() )
            using ( var writer = new BinaryWriter( stream ) )
            {
                action( writer );

                writer.Flush();

                var blob = stream.GetBuffer();

                var entity = new DynamicTableEntity( partitionKey, rowKey )
                {
                    Properties = new Dictionary<string, EntityProperty>
                    {
                        { "blob", new EntityProperty( blob ) }
                    }
                };

                var operation = TableOperation.InsertOrReplace( entity );

                await Task<TableResult>.Factory.FromAsync( table.BeginExecute, table.EndExecute, operation, null );
            }
        }

        private static async Task ReadBytes( CloudTable table, Action<byte[]> action, string partitionKey, string rowKey, bool checkExistence = true )
        {
            var awaitable = checkExistence
                                ? Task<bool>.Factory.FromAsync( table.BeginExists, table.EndExists, null )
                                : Task.FromResult( true );

            if ( await awaitable )
            {
                var operation = TableOperation.Retrieve<DynamicTableEntity>( partitionKey, rowKey );

                var result = await Task<TableResult>.Factory.FromAsync( table.BeginExecute, table.EndExecute, operation, null );

                Contract.Assert( result != null );

                if ( result.Result == null )
                {
                    return;
                }

                Contract.Assert( result.Result is DynamicTableEntity );

                var entity = (DynamicTableEntity) result.Result;

                var blob = entity[ "blob" ].BinaryValue;

                Contract.Assert( blob != null );

                action( blob );
            }
        }

        private static Task Read( CloudTable table, Action<BinaryReader> action, string partitionKey, string rowKey, bool checkExistence = true )
        {
            Action<byte[]> action2 = blob =>
            {
                using ( var stream = new MemoryStream( blob ) )
                using ( var reader = new BinaryReader( stream ) )
                {
                    action( reader );
                }
            };

            return ReadBytes( table, action2, partitionKey, rowKey, checkExistence );
        }

        public override async Task SerializeKeysAsync( Type type, Type keyType, IDictionary keyMap )
        {
            var pathLock = Lock.GetLock( type.FullName );

            using ( await pathLock.LockAsync() )
            {
                Action<BinaryWriter> action = writer =>
                {
                    writer.Write( keyMap.Count );

                    foreach ( var key in keyMap.Keys )
                    {
                        DatabaseSerializer.Serialize( key, writer );

                        writer.Write( (int) keyMap[ key ] );
                    }
                };

                await Store( _keysTable.Value, action, type.FullName, keyType.FullName );
            }

            await SerializeTypesAsync();
        }

        public override async Task<IDictionary> DeserializeKeysAsync( Type type, Type keyType, IDictionary dictionary )
        {
            var pathLock = Lock.GetLock( type.FullName );

            using ( await pathLock.LockAsync() )
            {
                Action<BinaryReader> action = reader =>
                {
                    var count = reader.ReadInt32();

                    for ( var x = 0; x < count; x++ )
                    {
                        dictionary.Add( DatabaseSerializer.Deserialize( keyType, reader ), reader.ReadInt32() );
                    }
                };

                await Read( _keysTable.Value, action, type.FullName, keyType.FullName );

                return dictionary;
            }
        }

        public override async Task SerializeIndexAsync<TKey, TIndex>( Type type, string indexName, Dictionary<TKey, TIndex> indexMap )
        {
            var pathLock = Lock.GetLock( type.FullName );

            using ( await pathLock.LockAsync() )
            {
                Action<BinaryWriter> action = writer =>
                {
                    writer.Write( indexMap.Count );

                    foreach ( var index in indexMap )
                    {
                        DatabaseSerializer.Serialize( index.Key, writer );
                        DatabaseSerializer.Serialize( index.Value, writer );
                    }
                };

                await Store( _indexesTable.Value, action, type.FullName, indexName );
            }
        }

        public override async Task SerializeIndexAsync<TKey, TIndex1, TIndex2>( Type type, string indexName, Dictionary<TKey, Tuple<TIndex1, TIndex2>> indexMap )
        {
            var pathLock = Lock.GetLock( type.FullName );

            using ( pathLock.LockAsync() )
            {
                Action<BinaryWriter> action = writer =>
                {
                    writer.Write( indexMap.Count );

                    foreach ( var index in indexMap )
                    {
                        DatabaseSerializer.Serialize( index.Key, writer );
                        DatabaseSerializer.Serialize( index.Value.Item1, writer );
                        DatabaseSerializer.Serialize( index.Value.Item2, writer );
                    }
                };

                await Store( _indexesTable.Value, action, type.FullName, indexName );
            }
        }

        public override async Task<Dictionary<TKey, TIndex>> DeserializeIndexAsync<TKey, TIndex>( Type type, string indexName )
        {
            var dictionary = new Dictionary<TKey, TIndex>();

            var pathLock = Lock.GetLock( type.FullName );

            using ( await pathLock.LockAsync() )
            {
                Action<BinaryReader> action = reader =>
                {
                    var count = reader.ReadInt32();

                    for ( var x = 0; x < count; x++ )
                    {
                        dictionary.Add( (TKey) DatabaseSerializer.Deserialize( typeof( TKey ), reader ),
                                        (TIndex) DatabaseSerializer.Deserialize( typeof( TIndex ), reader ) );
                    }
                };

                await Read( _indexesTable.Value, action, type.FullName, indexName );

                return dictionary;
            }
        }

        public override async Task<Dictionary<TKey, Tuple<TIndex1, TIndex2>>> DeserializeIndexAsync<TKey, TIndex1, TIndex2>( Type type, string indexName )
        {
            var dictionary = new Dictionary<TKey, Tuple<TIndex1, TIndex2>>();

            var pathLock = Lock.GetLock( type.FullName );

            using ( await pathLock.LockAsync() )
            {
                Action<BinaryReader> action = reader =>
                {
                    var count = reader.ReadInt32();

                    for ( var x = 0; x < count; x++ )
                    {
                        dictionary.Add( (TKey) DatabaseSerializer.Deserialize( typeof ( TKey ), reader ),
                                        Tuple.Create( (TIndex1) DatabaseSerializer.Deserialize( typeof ( TIndex1 ), reader ),
                                                      (TIndex2) DatabaseSerializer.Deserialize( typeof ( TIndex2 ), reader ) ) );
                    }
                };

                await Read( _indexesTable.Value, action, type.FullName, indexName );

                return dictionary;
            }
        }

        public override void PublishTables( Dictionary<Type, ITableDefinition> tables, Func<string, Type> resolveType )
        {
            if ( _typesTable.IsValueCreated == false )
            {
                return;
            }

            Action<BinaryReader> action = async reader =>
            {
                var count = reader.ReadInt32();

                for ( var x = 0; x < count; x++ )
                {
                    var fullTypeName = reader.ReadString();

                    var tableType = resolveType( fullTypeName );

                    if ( tableType == null )
                    {
                        throw new SterlingTableNotFoundException( fullTypeName, DatabaseName );
                    }

                    await GetTypeIndexAsync( tableType.AssemblyQualifiedName );
                }
            };

            Read( _typesTable.Value, action, "___Types", "___Types" ).Wait();
        }

        public override async Task SerializeTypesAsync()
        {
            var pathLock = Lock.GetLock( TypeIndex.GetType().FullName );

            using ( await pathLock.LockAsync() )
            {
                Action<BinaryWriter> action = writer =>
                {
                    writer.Write( TypeIndex.Count );

                    foreach ( var type in TypeIndex )
                    {
                        writer.Write( type );
                    }
                };

                await Store( _typesTable.Value, action, "___Types", "___Types" );
            }
        }

        public override async Task<int> GetTypeIndexAsync( string type )
        {
            var pathLock = Lock.GetLock( TypeIndex.GetType().FullName );

            using ( await pathLock.LockAsync() )
            {
                if ( !TypeIndex.Contains( type ) )
                {
                    TypeIndex.Add( type );
                    _dirtyType = true;
                }

                return TypeIndex.IndexOf( type );
            }
        }

        public override Task<string> GetTypeAtIndexAsync( int index )
        {
            return Task.FromResult( TypeIndex[ index ] );
        }

        public override async Task SaveAsync( Type type, int keyIndex, byte[] bytes )
        {
            var pathLock = Lock.GetLock( string.Format( "{0}-{1}", type.FullName, keyIndex ) );

            using ( await pathLock.LockAsync() )
            {
                Action<BinaryWriter> action = writer => writer.Write( bytes );

                await Store( _typesTable.Value, action, type.FullName, keyIndex.ToString() );
            }

            if ( !_dirtyType ) return;

            _dirtyType = false;

            await SerializeTypesAsync();
        }

        public override async Task<BinaryReader> LoadAsync( Type type, int keyIndex )
        {
            var pathLock = Lock.GetLock( string.Format( "{0}-{1}", type.FullName, keyIndex ) );

            using ( await pathLock.LockAsync() )
            {
                byte[] bytes = null;

                Action<byte[]> action = bytesArg => bytes = bytesArg;

                await ReadBytes( _typesTable.Value, action, type.FullName, keyIndex.ToString() );

                Contract.Assert( bytes != null );

                return new BinaryReader( new MemoryStream( bytes ) );
            }
        }

        public override async Task DeleteAsync( Type type, int keyIndex )
        {
            var pathLock = Lock.GetLock( string.Format( "{0}-{1}", type.FullName, keyIndex ) );

            using ( await pathLock.LockAsync() )
            {
                var operation = TableOperation.Delete( new DynamicTableEntity( type.FullName, keyIndex.ToString() ) );

                await Task<TableResult>.Factory.FromAsync( _typesTable.Value.BeginExecute, _typesTable.Value.EndExecute, operation, null );
            }
        }

        public override async Task TruncateAsync( Type type )
        {
            var queryText = TableQuery.GenerateFilterCondition( "PartitionKey", QueryComparisons.Equal, type.FullName );

            var query = new TableQuery<DynamicTableEntity>().Where( queryText );

            var records = await _typesTable.Value.ExecuteQueryAsync( query );

            foreach ( var record in records )
            {
                var rowKey = record.RowKey;

                var pathLock = Lock.GetLock( string.Format( "{0}-{1}", type.FullName, rowKey ) );

                using ( await pathLock.LockAsync() )
                {
                    var operation = TableOperation.Delete( new DynamicTableEntity( type.FullName, rowKey ) );

                    await Task<TableResult>.Factory.FromAsync( _typesTable.Value.BeginExecute, _typesTable.Value.EndExecute, operation, null );
                }
            }
        }

        public override async Task PurgeAsync()
        {
            await PurgeAsync( _keysTable );
            await PurgeAsync( _indexesTable );
            await PurgeAsync( _typesTable );
        }

        private async Task PurgeAsync( Lazy<CloudTable> table )
        {
            if ( table.IsValueCreated )
            {
                if ( await Task<bool>.Factory.FromAsync( table.Value.BeginExists, table.Value.EndExists, null ) )
                {
                    await Task.Factory.FromAsync( table.Value.BeginDelete, table.Value.EndDelete, null );
                    await Task.Factory.FromAsync( table.Value.BeginCreate, table.Value.EndCreate, null );
                }
            }
        }
    }
}
