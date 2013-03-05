﻿
#if NETFX_CORE
using Wintellect.Sterling.WinRT.WindowsStorage;
using Microsoft.VisualStudio.TestPlatform.UnitTestFramework;
using Wintellect.Sterling.WinRT;
#elif SILVERLIGHT
using Microsoft.Phone.Testing;
using Wintellect.Sterling.WP8.IsolatedStorage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Wintellect.Sterling.WP8;
#else
using Wintellect.Sterling.Server;
using Wintellect.Sterling.Server.FileSystem;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Wintellect.Sterling.Server;
#endif

using System;
using System.Linq;

using Wintellect.Sterling.Core;
using Wintellect.Sterling.Core.Database;
using Wintellect.Sterling.Core.Serialization;
using Wintellect.Sterling.Test.Helpers;

namespace Wintellect.Sterling.Test.Database
{
#if SILVERLIGHT
    [Tag("TableDefinition")]
#endif
    [TestClass]
    public class TestTableDefinitionAltDriver : TestTableDefinition
    {
        protected override ISterlingDriver GetDriver( string test )
        {
#if NETFX_CORE
            return new WindowsStorageDriver( test );
#elif SILVERLIGHT
            return new IsolatedStorageDriver( test );
#elif AZURE_DRIVER
            return new Wintellect.Sterling.Server.Azure.TableStorage.Driver();
#else
            return new FileSystemDriver( test );
#endif
        }

        protected override ISterlingDriver GetDriver( string test, string databaseName, ISterlingSerializer serializer )
        {
#if NETFX_CORE
            return new WindowsStorageDriver( test + databaseName, serializer, ( lvl, msg, ex ) => { } );
#elif SILVERLIGHT
            return new IsolatedStorageDriver( test + databaseName, serializer, ( lvl, msg, ex ) => { } );
#elif AZURE_DRIVER
            return new Wintellect.Sterling.Server.Azure.TableStorage.Driver( test + databaseName, serializer, ( lvl, msg, ex ) => { } );
#else
            return new FileSystemDriver( test + databaseName, serializer, ( lvl, msg, ex ) => { } );
#endif
        }
    }

#if SILVERLIGHT
    [Tag("TableDefinition")]
#endif
    [TestClass]
    public class TestTableDefinition : TestBase
    {
        protected virtual ISterlingDriver GetDriver( string test, string databaseName, ISterlingSerializer serializer )
        {
            return new MemoryDriver( test + databaseName, serializer );
        }

        private readonly TestModel[] _models = new[]
                                          {
                                              TestModel.MakeTestModel(), TestModel.MakeTestModel(),
                                              TestModel.MakeTestModel()
                                          };

        private TableDefinition<TestModel, int> _target;
        private readonly ISterlingDatabaseInstance _testDatabase = new TestDatabaseInterfaceInstance();
        private int _testAccessCount;

        public TestContext TestContext { get; set; }

        /// <summary>
        ///     Fetcher - also flag the fetch
        /// </summary>
        /// <param name="key">The key</param>
        /// <returns>The model</returns>
        private TestModel _GetTestModelByKey(int key)
        {
            _testAccessCount++;
            return (from t in _models where t.Key.Equals(key) select t).FirstOrDefault();
        }

        [TestInitialize]
        public void TestInit()
        {
            var serializer = new AggregateSerializer( new PlatformAdapter() );
            serializer.AddSerializer(new DefaultSerializer());
            serializer.AddSerializer(new ExtendedSerializer( new PlatformAdapter() ));
            _testAccessCount = 0;
            _target = new TableDefinition<TestModel, int>(GetDriver(TestContext.TestName, _testDatabase.Name, serializer),
                                                        _GetTestModelByKey, t => t.Key);
        }        

        [TestMethod]
        public void TestConstruction()
        {
            Assert.AreEqual(typeof(TestModel), _target.TableType, "Table type mismatch.");
            Assert.AreEqual(typeof(int), _target.KeyType, "Key type mismatch.");
            var key = _target.FetchKey(_models[1]);
            Assert.AreEqual(_models[1].Key, key, "Key mismatch after fetch key invoked.");
        }
    }
}
