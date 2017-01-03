////////////////////////////////////////////////////////////////////////////
//
// Copyright 2016 Realm Inc.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
////////////////////////////////////////////////////////////////////////////

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
#if ENABLE_INTERNAL_NON_PCL_TESTS
using Nito.AsyncEx;
#endif
using NUnit.Framework;
using Realms;

namespace IntegrationTests
{
    [TestFixture, Preserve(AllMembers = true)]
    public class AsyncTests : RealmTest
    {
        private Lazy<Realm> _lazyRealm;

        private Realm _realm => _lazyRealm.Value;

        // We capture the current SynchronizationContext when opening a Realm.
        // However, NUnit replaces the SynchronizationContext after the SetUp method and before the async test method.
        // That's why we make sure we open the Realm in the test method by accessing it lazily.
        public override void SetUp()
        {
            base.SetUp();
            _lazyRealm = new Lazy<Realm>(() => Realm.GetInstance());
        }

        public override void TearDown()
        {
            if (_lazyRealm.IsValueCreated)
            {
                _realm.Dispose();
                Realm.DeleteRealm(_realm.Config);
            }

            base.TearDown();
        }

        #if ENABLE_INTERNAL_NON_PCL_TESTS
        [Test]
        public void AsyncWrite_ShouldExecuteOnWorkerThread()
        {
            AsyncContext.Run(async delegate
            {
                var currentThreadId = Thread.CurrentThread.ManagedThreadId;
                var otherThreadId = currentThreadId;

                Assert.That(_realm.All<Person>().Count(), Is.EqualTo(0));
                await _realm.WriteAsync(realm =>
                {
                    otherThreadId = Thread.CurrentThread.ManagedThreadId;
                    realm.CreateObject<Person>();
                });

                await Task.Yield();

                Assert.That(_realm.All<Person>().Count(), Is.EqualTo(1));
                Assert.That(otherThreadId, Is.Not.EqualTo(currentThreadId));
            });
        }
        #endif

        internal class MyDataObject : RealmObject
        {
            [PrimaryKey]
            public string Path { get; set; }

            public int? ExpensiveToComputeValue { get; set; }
        }

        #if ENABLE_INTERNAL_NON_PCL_TESTS
        [Test]
        public void AsyncWrite_UpdateViaPrimaryKey()
        {
            AsyncContext.Run(async delegate
            {
                var path = "/path/to/some/item";
                MyDataObject obj = null;
                _realm.Write(() =>
                {
                    obj = _realm.CreateObject<MyDataObject>();
                    obj.Path = path;
                });

                await _realm.WriteAsync(realm =>
                {
                    var dataObj = realm.Find<MyDataObject>(path);
                    dataObj.ExpensiveToComputeValue = 123; // imagine this was a very CPU-intensive operation
                });

                await Task.Yield();

                Assert.That(obj.ExpensiveToComputeValue, Is.Not.Null);
            });
        }
        #endif // ENABLE_INTERNAL_NON_PCL_TESTS
    }
}