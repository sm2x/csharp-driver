//
//      Copyright (C) 2012 DataStax Inc.
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
//
﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace Cassandra.Data.Linq
{
    public class ContextTable<TEntity> : Table<TEntity>
    {
        private Context _context;

        internal ContextTable(Table<TEntity> table, Context context) : base(table)
        {
            _context = context;
        }

        public void Insert(TEntity entity, EntityTrackingMode trmod = EntityTrackingMode.DetachAfterSave)
        {
            AddNew(entity, trmod);
        }
        
        public void Attach(TEntity entity, EntityUpdateMode updmod = EntityUpdateMode.AllOrNone, EntityTrackingMode trmod = EntityTrackingMode.KeepAttachedAfterSave)
        {
            _context.Attach(this, entity, updmod, trmod);
        }

        public void Detach(TEntity entity)
        {
            _context.Detach(this, entity);
        }

        public void Delete(TEntity entity)
        {
            _context.Delete(this, entity);
        }

        public void AddNew(TEntity entity, EntityTrackingMode trmod = EntityTrackingMode.DetachAfterSave)
        {
            _context.AddNew(this, entity, trmod);
        }

        public void EnableQueryTracing(TEntity entity, bool enable = true)
        {
            _context.EnableQueryTracing(this, entity, enable);
        }

        public List<QueryTrace> RetriveAllQueryTraces()
        {
            return _context.RetriveAllQueryTraces(this);
        }

        public QueryTrace RetriveQueryTrace( TEntity entity)
        {
            return _context.RetriveQueryTrace(this, entity);
        }
    }


    public class Context
    {
        private struct CqlSaveTag
        {
            public Dictionary<string, TableType> TableTypes;
            public TableType TableType;
            public List<CqlCommand> NewAdditionalCommands;
        }

        private Session _managedSession = null;

        private string _keyspaceName;

        /// <summary>
        /// Gets name of keyspace.
        /// </summary>
        public string Keyspace
        {
            get { return _keyspaceName; }
        }

        private void Initialize(Session cqlConnection)
        {
            if (cqlConnection == null)
                return;
            this._managedSession = cqlConnection;
            this._keyspaceName = cqlConnection.Keyspace;
        }

        public Context(Session cqlSession)
        {
            Initialize(cqlSession);
        }

        private readonly Dictionary<string, ITable> _tables = new Dictionary<string, ITable>();
        private readonly Dictionary<string, IMutationTracker> _mutationTrackers = new Dictionary<string, IMutationTracker>();

        public void CreateTablesIfNotExist()
        {
            foreach (var table in _tables)
            {
                try
                {
                    table.Value.Create();
                }
                catch (AlreadyExistsException)
                {
                    //already exists
                }
            }
        }

        internal static string QuotedGlobalTableName(string calcedTableName, string keyspaceName)
        {
            if (keyspaceName != null)
                return keyspaceName.QuoteIdentifier() + "." + calcedTableName.QuoteIdentifier();
            else
                return calcedTableName.QuoteIdentifier();
        }

        public ContextTable<TEntity> AddTable<TEntity>(string tableName = null,string keyspaceName = null) where TEntity : class
        {
            var tn = QuotedGlobalTableName(Table<TEntity>.CalculateName(tableName),keyspaceName);
            if (_tables.ContainsKey(tn))
                return new ContextTable<TEntity>((Table<TEntity>)_tables[tn], this);
            else
            {
                var table = new Table<TEntity>(_managedSession, tableName, keyspaceName);
                _tables.Add(tn, table);
                _mutationTrackers.Add(tn, new MutationTracker<TEntity>());
                return new ContextTable<TEntity>(table, this);
            }
        }

        public bool HasTable<TEntity>(string tableName = null, string keyspaceName = null) where TEntity : class
        {
            var tn = QuotedGlobalTableName(Table<TEntity>.CalculateName(tableName), keyspaceName);
            return _tables.ContainsKey(tn);
        }

        public ContextTable<TEntity> GetTable<TEntity>(string tableName = null, string keyspaceName = null) where TEntity : class
        {
            var tn = QuotedGlobalTableName(Table<TEntity>.CalculateName(tableName), keyspaceName);
            return new ContextTable<TEntity>((Table<TEntity>)_tables[tn], this);
        }

        internal RowSet ExecuteReadQuery(string cqlQuery, ConsistencyLevel consistencyLevel,  bool enableTraceing)
        {
            return _managedSession.Execute(new SimpleStatement(cqlQuery).EnableTracing(enableTraceing).SetConsistencyLevel(consistencyLevel));
        }

        internal IAsyncResult BeginExecuteReadQuery(string cqlQuery, ConsistencyLevel consistencyLevel, bool enableTraceing, object tag, AsyncCallback callback, object state)
        {
            return _managedSession.BeginExecute(new SimpleStatement(cqlQuery).EnableTracing(enableTraceing).SetConsistencyLevel(consistencyLevel), tag, callback, state);
        }

        internal RowSet EndExecuteReadQuery(IAsyncResult ar)
        {
            return _managedSession.EndExecute(ar);
        }

        internal RowSet ExecuteWriteQuery(string cqlQuery, object[] values, ConsistencyLevel consistencyLevel, bool enableTraceing)
        {
            var statement = _managedSession.Prepare(cqlQuery);
            return _managedSession.Execute(statement.Bind(values).EnableTracing(enableTraceing).SetConsistencyLevel(consistencyLevel));
        }

        internal IAsyncResult BeginExecuteWriteQuery(string cqlQuery, object[] values, ConsistencyLevel consistencyLevel, bool enableTraceing, object tag, AsyncCallback callback, object state)
        {
            return _managedSession.BeginExecute(new SimpleStatement(cqlQuery).BindObjects(values).EnableTracing(enableTraceing).SetConsistencyLevel(consistencyLevel), tag, callback, state);
        }

        internal RowSet EndExecuteWriteQuery(IAsyncResult ar)
        {
            return _managedSession.EndExecute(ar);
        }

        public IAsyncResult BeginSaveChangesBatch(TableType tableType, ConsistencyLevel consistencyLevel, AsyncCallback callback, object state)
        {
            string cql;
            if (_managedSession.BinaryProtocolVersion > 1)
                return BeginSaveChangesBatchV2(tableType, consistencyLevel, callback, state);
            else
                return BeginSaveChangesBatchV1(tableType, consistencyLevel, callback, state, out cql);
        }

        public override string ToString()
        {
            string cql;
            BeginSaveChangesBatchV1(TableType.Standard, ConsistencyLevel.Any, null, null, out cql);
            return cql;
        }
        
        IAsyncResult BeginSaveChangesBatchV1(TableType tableType, ConsistencyLevel consistencyLevel, AsyncCallback callback, object state,out string cql)
        {
            cql = null;
            if (tableType == TableType.All)
                throw new ArgumentOutOfRangeException("tableType");

            var tableTypes = new Dictionary<string, TableType>();

            foreach (var table in _tables)
            {
                bool isCounter = false;
                var props = table.Value.GetEntityType().GetPropertiesOrFields();
                foreach (var prop in props)
                {
                    Type tpy = prop.GetTypeFromPropertyOrField();
                    if (
                        prop.GetCustomAttributes(typeof(CounterAttribute), true).FirstOrDefault() as
                        CounterAttribute != null)
                    {
                        isCounter = true;
                        break;
                    }
                }
                tableTypes.Add(table.Key, isCounter ? TableType.Counter : TableType.Standard);
            }

            var newAdditionalCommands = new List<CqlCommand>();
            if (((tableType & TableType.Counter) == TableType.Counter))
            {
                bool enableTracing = false;
                var counterBatchScript = new StringBuilder();
                foreach (var table in _tables)
                    if (tableTypes[table.Key] == TableType.Counter)
                        enableTracing |= _mutationTrackers[table.Key].AppendChangesToBatch(counterBatchScript, table.Key);

                foreach (var additional in _additionalCommands)
                    if (tableTypes[additional.GetTable().GetQuotedTableName()] == TableType.Counter)
                    {
                        counterBatchScript.AppendLine(additional.ToString() + ";");
                        enableTracing |= additional.IsTracing;
                    }
                    else if (tableType == TableType.Counter)
                        newAdditionalCommands.Add(additional);

                if (counterBatchScript.Length != 0)
                {
                    cql = "BEGIN COUNTER BATCH\r\n" + counterBatchScript.ToString() + "\r\nAPPLY BATCH";
                    return BeginExecuteWriteQuery(
                        cql, null, consistencyLevel, enableTracing,
                            new CqlSaveTag() { TableTypes = tableTypes, TableType = TableType.Counter, NewAdditionalCommands = newAdditionalCommands }, callback, state);
                }
            }


            if (((tableType & TableType.Standard) == TableType.Standard))
            {
                bool enableTracing = false;
                var batchScript = new StringBuilder();
                foreach (var table in _tables)
                    if (tableTypes[table.Key] == TableType.Standard)
                        enableTracing |= _mutationTrackers[table.Key].AppendChangesToBatch(batchScript, table.Key);

                foreach (var additional in _additionalCommands)
                    if (tableTypes[additional.GetTable().GetQuotedTableName()] == TableType.Standard)
                    {
                        enableTracing |= additional.IsTracing;
                        batchScript.AppendLine(additional.ToString() + ";");
                    }
                    else if (tableType == TableType.Standard)
                        newAdditionalCommands.Add(additional);

                if (batchScript.Length != 0)
                {
                    cql ="BEGIN BATCH\r\n" + batchScript.ToString() + "APPLY BATCH";
                    return BeginExecuteWriteQuery(cql, null, consistencyLevel, enableTracing,
                        new CqlSaveTag() { TableTypes = tableTypes, TableType = TableType.Standard, NewAdditionalCommands = newAdditionalCommands }, callback, state);
                }
            }

            return null;
        }

        IAsyncResult BeginSaveChangesBatchV2(TableType tableType, ConsistencyLevel consistencyLevel, AsyncCallback callback, object state)
        {
            if (tableType == TableType.All)
                throw new ArgumentOutOfRangeException("tableType");

            var tableTypes = new Dictionary<string, TableType>();

            foreach (var table in _tables)
            {
                bool isCounter = false;
                var props = table.Value.GetEntityType().GetPropertiesOrFields();
                foreach (var prop in props)
                {
                    Type tpy = prop.GetTypeFromPropertyOrField();
                    if (
                        prop.GetCustomAttributes(typeof(CounterAttribute), true).FirstOrDefault() as
                        CounterAttribute != null)
                    {
                        isCounter = true;
                        break;
                    }
                }
                tableTypes.Add(table.Key, isCounter ? TableType.Counter : TableType.Standard);
            }

            var newAdditionalCommands = new List<CqlCommand>();
            if (((tableType & TableType.Counter) == TableType.Counter))
            {
                bool enableTracing = false;
                var counterBatchScript = new BatchStatement();
                foreach (var table in _tables)
                    if (tableTypes[table.Key] == TableType.Counter)
                        enableTracing |= _mutationTrackers[table.Key].AppendChangesToBatch(counterBatchScript, table.Key);

                foreach (var additional in _additionalCommands)
                    if (tableTypes[additional.GetTable().GetQuotedTableName()] == TableType.Counter)
                    {
                        counterBatchScript.AddQuery(additional);
                        enableTracing |= additional.IsTracing;
                    }
                    else if (tableType == TableType.Counter)
                        newAdditionalCommands.Add(additional);

                if (!counterBatchScript.IsEmpty)
                {
                    return _managedSession.BeginExecute(counterBatchScript.SetBatchType(BatchType.Counter).SetConsistencyLevel(consistencyLevel).EnableTracing(enableTracing),
                            new CqlSaveTag() { TableTypes = tableTypes, TableType = TableType.Counter, NewAdditionalCommands = newAdditionalCommands }, callback, state);
                }
            }


            if (((tableType & TableType.Standard) == TableType.Standard))
            {
                bool enableTracing = false;
                var batchScript = new BatchStatement();
                foreach (var table in _tables)
                    if (tableTypes[table.Key] == TableType.Standard)
                        enableTracing |= _mutationTrackers[table.Key].AppendChangesToBatch(batchScript, table.Key);

                foreach (var additional in _additionalCommands)
                    if (tableTypes[additional.GetTable().GetQuotedTableName()] == TableType.Standard)
                    {
                        enableTracing |= additional.IsTracing;
                        batchScript.AddQuery(additional);
                    }
                    else if (tableType == TableType.Standard)
                        newAdditionalCommands.Add(additional);

                if (!batchScript.IsEmpty)
                {
                    return _managedSession.BeginExecute(batchScript.SetBatchType(BatchType.Logged).SetConsistencyLevel(consistencyLevel).EnableTracing(enableTracing),
                        new CqlSaveTag() { TableTypes = tableTypes, TableType = TableType.Standard, NewAdditionalCommands = newAdditionalCommands }, callback, state);
                }
            }

            return null;
        }

        public void EndSaveChangesBatch(IAsyncResult ar)
        {
            var res = EndExecuteWriteQuery(ar);

            var tag = (CqlSaveTag) Session.GetTag(ar);
            foreach (var table in _tables)
                if (tag.TableTypes[table.Key] == tag.TableType)
                    _mutationTrackers[table.Key].BatchCompleted(res.Info.QueryTrace);
            _additionalCommands = tag.NewAdditionalCommands;
        }
        public void SaveChanges(ConsistencyLevel consistencyLevel ,SaveChangesMode mode = SaveChangesMode.Batch, TableType tableType = TableType.All)
        {
            saveChanges(consistencyLevel, mode, tableType);
        }

        /// <summary>
        /// With default consistency level.
        /// </summary>
        /// <param name="mode"></param>
        /// <param name="tableType"></param>
        public void SaveChanges(SaveChangesMode mode = SaveChangesMode.Batch, TableType tableType = TableType.All)
        {
            saveChanges(_managedSession.Cluster.Configuration.QueryOptions.GetConsistencyLevel(), mode, tableType);
        }

        private void saveChanges(ConsistencyLevel consistencyLevel, SaveChangesMode mode, TableType tableType)
        {
            var newAdditionalCommands = new List<CqlCommand>();

            if (mode == SaveChangesMode.OneByOne)
            {
                foreach (var table in _tables)
                    if ((((tableType & TableType.Counter) == TableType.Counter) &&
                         table.Value.GetTableType() == TableType.Counter)
                        ||
                        (((tableType & TableType.Standard) == TableType.Standard) &&
                         table.Value.GetTableType() == TableType.Standard))
                        _mutationTrackers[table.Key].SaveChangesOneByOne(this, table.Key, consistencyLevel);

                foreach (var additional in _additionalCommands)
                {
                    if ((tableType & TableType.Counter) == TableType.Counter)
                    {
                        if (additional.GetTable().GetTableType() == TableType.Counter)
                            additional.Execute();
                        else if (tableType == TableType.Counter)
                            newAdditionalCommands.Add(additional);
                    }
                    else if ((tableType & TableType.Standard) == TableType.Standard)
                    {
                        if (additional.GetTable().GetTableType() == TableType.Standard)
                            additional.Execute();
                        else if (tableType == TableType.Standard)
                            newAdditionalCommands.Add(additional);
                    }
                }
            }
            else
            {
                if (tableType.HasFlag(TableType.Counter))
                {
                    var ar = BeginSaveChangesBatch(TableType.Counter, consistencyLevel, null, null);
                    if(ar!=null)
                        EndSaveChangesBatch(ar);
                }
                if (tableType.HasFlag(TableType.Standard))
                {
                    var ar = BeginSaveChangesBatch(TableType.Standard, consistencyLevel, null, null);
                    if (ar != null)
                        EndSaveChangesBatch(ar);
                }
            }
            _additionalCommands = newAdditionalCommands;
        }

        internal List<CqlCommand> _additionalCommands = new List<CqlCommand>();

        public void AppendCommand(CqlCommand cqlCommand)
        {
            _additionalCommands.Add(cqlCommand);
        }

        public void EnableQueryTracing<TEntity>(Table<TEntity> table, TEntity entity, bool enable = true)
        {
            (_mutationTrackers[table.GetQuotedTableName()] as MutationTracker<TEntity>).EnableQueryTracing(entity, enable);
        }

        public List<QueryTrace> RetriveAllQueryTraces(ITable table)
        {
            return _mutationTrackers[table.GetQuotedTableName()].RetriveAllQueryTraces();
        }

        public QueryTrace RetriveQueryTrace<TEntity>(Table<TEntity> table, TEntity entity)
        {
            return (_mutationTrackers[table.GetQuotedTableName()] as MutationTracker<TEntity>).RetriveQueryTrace(entity);
        }

        public void Attach<TEntity>(Table<TEntity> table, TEntity entity, EntityUpdateMode updmod = EntityUpdateMode.AllOrNone, EntityTrackingMode trmod = EntityTrackingMode.KeepAttachedAfterSave)
        {
            (_mutationTrackers[table.GetQuotedTableName()] as MutationTracker<TEntity>).Attach(entity, updmod, trmod);
        }

        public void Detach<TEntity>(Table<TEntity> table, TEntity entity)
        {
            (_mutationTrackers[table.GetQuotedTableName()] as MutationTracker<TEntity>).Detach(entity);
        }

        public void Delete<TEntity>(Table<TEntity> table, TEntity entity)
        {
            (_mutationTrackers[table.GetQuotedTableName()] as MutationTracker<TEntity>).Delete(entity);
        }

        public void AddNew<TEntity>(Table<TEntity> table, TEntity entity, EntityTrackingMode trmod = EntityTrackingMode.DetachAfterSave)
        {
            (_mutationTrackers[table.GetQuotedTableName()] as MutationTracker<TEntity>).AddNew(entity, trmod);
        }
    }
}
