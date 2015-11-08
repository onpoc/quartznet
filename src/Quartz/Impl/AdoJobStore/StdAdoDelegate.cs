#region License

/* 
 * All content copyright Terracotta, Inc., unless otherwise indicated. All rights reserved. 
 * 
 * Licensed under the Apache License, Version 2.0 (the "License"); you may not 
 * use this file except in compliance with the License. You may obtain a copy 
 * of the License at 
 * 
 *   http://www.apache.org/licenses/LICENSE-2.0 
 *   
 * Unless required by applicable law or agreed to in writing, software 
 * distributed under the License is distributed on an "AS IS" BASIS, WITHOUT 
 * WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the 
 * License for the specific language governing permissions and limitations 
 * under the License.
 * 
 */

#endregion

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;

using Quartz.Impl.AdoJobStore.Common;
using Quartz.Impl.Matchers;
using Quartz.Impl.Triggers;
using Quartz.Logging;
using Quartz.Spi;
using Quartz.Util;

namespace Quartz.Impl.AdoJobStore
{
    /// <summary>
    /// This is meant to be an abstract base class for most, if not all, <see cref="IDriverDelegate" />
    /// implementations. Subclasses should override only those methods that need
    /// special handling for the DBMS driver in question.
    /// </summary>
    /// <author><a href="mailto:jeff@binaryfeed.org">Jeffrey Wescott</a></author>
    /// <author>James House</author>
    /// <author>Marko Lahma (.NET)</author>
    public class StdAdoDelegate : StdAdoConstants, IDriverDelegate, IDbAccessor
    {
        private ILog logger;
        private string tablePrefix = DefaultTablePrefix;
        private string instanceId;
        private string schedName;
        private bool useProperties;
        private IDbProvider dbProvider;
        private ITypeLoadHelper typeLoadHelper;
        private AdoUtil adoUtil;
        private readonly IList<ITriggerPersistenceDelegate> triggerPersistenceDelegates = new List<ITriggerPersistenceDelegate>();
        private string schedNameLiteral;
        private IObjectSerializer objectSerializer;

        /// <summary>
        /// Initializes the driver delegate.
        /// </summary>
        public virtual void Initialize(DelegateInitializationArgs args)
        {
            logger = args.Logger;
            tablePrefix = args.TablePrefix;
            schedName = args.InstanceName;
            instanceId = args.InstanceId;
            dbProvider = args.DbProvider;
            typeLoadHelper = args.TypeLoadHelper;
            useProperties = args.UseProperties;
            adoUtil = new AdoUtil(args.DbProvider);
            objectSerializer = args.ObjectSerializer;

            AddDefaultTriggerPersistenceDelegates();

            if (!string.IsNullOrEmpty(args.InitString))
            {
                string[] settings = args.InitString.Split('\\', '|');

                foreach (string setting in settings)
                {
                    var index = setting.IndexOf('=');
                    if (index == -1 || index == setting.Length - 1)
                    {
                        continue;
                    }

                    string name = setting.Substring(0, index).Trim();
                    string value = setting.Substring(index + 1).Trim();

                    if (string.IsNullOrEmpty(value))
                    {
                        continue;
                    }

                    // we support old *Classes and new *Types, latter has better support for assembly qualified names
                    if (name.Equals("triggerPersistenceDelegateClasses") || name.Equals("triggerPersistenceDelegateTypes"))
                    {
                        var separator = ',';
                        if (value.IndexOf(';') != -1 || name.Equals("triggerPersistenceDelegateTypes"))
                        {
                            // use separator that allows assembly qualified names
                            separator = ';';
                        }

                        string[] trigDelegates = value.Split(separator);

                        foreach (string triggerTypeName in trigDelegates)
                        {
                            var typeName = triggerTypeName.Trim();

                            if (string.IsNullOrEmpty(typeName))
                            {
                                continue;
                            }

                            try
                            {
                                Type trigDelClass = typeLoadHelper.LoadType(typeName);
                                AddTriggerPersistenceDelegate((ITriggerPersistenceDelegate) Activator.CreateInstance(trigDelClass));
                            }
                            catch (Exception e)
                            {
                                throw new NoSuchDelegateException("Error instantiating TriggerPersistenceDelegate of type: " + triggerTypeName, e);
                            }
                        }
                    }
                    else
                    {
                        throw new NoSuchDelegateException("Unknown setting: '" + name + "'");
                    }
                }
            }
        }

        protected virtual void AddDefaultTriggerPersistenceDelegates()
        {
            AddTriggerPersistenceDelegate(new SimpleTriggerPersistenceDelegate());
            AddTriggerPersistenceDelegate(new CronTriggerPersistenceDelegate());
            AddTriggerPersistenceDelegate(new CalendarIntervalTriggerPersistenceDelegate());
            AddTriggerPersistenceDelegate(new DailyTimeIntervalTriggerPersistenceDelegate());
        }

        protected virtual bool CanUseProperties
        {
            get { return useProperties; }
        }

        public virtual void AddTriggerPersistenceDelegate(ITriggerPersistenceDelegate del)
        {
            logger.Debug("Adding TriggerPersistenceDelegate of type: " + del.GetType());
            del.Initialize(tablePrefix, schedName, this);
            triggerPersistenceDelegates.Add(del);
        }

        public virtual ITriggerPersistenceDelegate FindTriggerPersistenceDelegate(IOperableTrigger trigger)
        {
            return triggerPersistenceDelegates.FirstOrDefault(del => del.CanHandleTriggerType(trigger));
        }

        public virtual ITriggerPersistenceDelegate FindTriggerPersistenceDelegate(string discriminator)
        {
            return triggerPersistenceDelegates.FirstOrDefault(del => del.GetHandledTriggerTypeDiscriminator() == discriminator);
        }

        //---------------------------------------------------------------------------
        // startup / recovery
        //---------------------------------------------------------------------------

        /// <summary>
        /// Insert the job detail record.
        /// </summary>
        /// <param name="conn">the DB Connection</param>
        /// <param name="newState">the new state for the triggers</param>
        /// <param name="oldState1">the first old state to update</param>
        /// <param name="oldState2">the second old state to update</param>
        /// <returns>number of rows updated</returns>
        public virtual async Task<int> UpdateTriggerStatesFromOtherStatesAsync(ConnectionAndTransactionHolder conn, string newState,
            string oldState1,
            string oldState2)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlUpdateTriggerStatesFromOtherStates)))
            {
                AddCommandParameter(cmd, "newState", newState);
                AddCommandParameter(cmd, "oldState1", oldState1);
                AddCommandParameter(cmd, "oldState2", oldState2);
                return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Get the names of all of the triggers that have misfired.
        /// </summary>
        /// <param name="conn">the DB Connection</param>
        /// <param name="ts">The ts.</param>
        /// <returns>an array of <see cref="TriggerKey" /> objects</returns>
        public virtual async Task<IReadOnlyList<TriggerKey>> SelectMisfiredTriggersAsync(ConnectionAndTransactionHolder conn, DateTimeOffset ts)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectMisfiredTriggers)))
            {
                AddCommandParameter(cmd, "timestamp", GetDbDateTimeValue(ts));
                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    List<TriggerKey> list = new List<TriggerKey>();
                    while (await rs.ReadAsync().ConfigureAwait(false))
                    {
                        string triggerName = rs.GetString(ColumnTriggerName);
                        string groupName = rs.GetString(ColumnTriggerGroup);
                        list.Add(new TriggerKey(triggerName, groupName));
                    }
                    return list;
                }
            }
        }

        /// <summary> 
        /// Select all of the triggers in a given state.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="state">The state the triggers must be in</param>
        /// <returns> an array of trigger <see cref="TriggerKey" />s </returns>
        public virtual async Task<IReadOnlyList<TriggerKey>> SelectTriggersInStateAsync(ConnectionAndTransactionHolder conn, string state)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectTriggersInState)))
            {
                AddCommandParameter(cmd, "state", state);
                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    List<TriggerKey> list = new List<TriggerKey>();
                    while (await rs.ReadAsync().ConfigureAwait(false))
                    {
                        list.Add(new TriggerKey(rs.GetString(0), rs.GetString(1)));
                    }

                    return list;
                }
            }
        }

        /// <summary>
        /// Get the names of all of the triggers in the given state that have
        /// misfired - according to the given timestamp.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="state">The state.</param>
        /// <param name="ts">The time stamp.</param>
        /// <returns>An array of <see cref="TriggerKey" /> objects</returns>
        public virtual async Task<IReadOnlyList<TriggerKey>> HasMisfiredTriggersInStateAsync(ConnectionAndTransactionHolder conn, string state, DateTimeOffset ts)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectMisfiredTriggersInState)))
            {
                AddCommandParameter(cmd, "timestamp", GetDbDateTimeValue(ts));
                AddCommandParameter(cmd, "state", state);

                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    List<TriggerKey> list = new List<TriggerKey>();
                    while (await rs.ReadAsync().ConfigureAwait(false))
                    {
                        string triggerName = rs.GetString(ColumnTriggerName);
                        string groupName = rs.GetString(ColumnTriggerGroup);
                        list.Add(new TriggerKey(triggerName, groupName));
                    }
                    return list;
                }
            }
        }

        /// <summary>
        /// Get the names of all of the triggers in the given state that have
        /// misfired - according to the given timestamp.  No more than count will
        /// be returned.
        /// </summary>
        /// <param name="conn">The conn.</param>
        /// <param name="state1">The state1.</param>
        /// <param name="ts">The ts.</param>
        /// <param name="count">The most misfired triggers to return, negative for all</param>
        /// <param name="resultList">
        ///   Output parameter.  A List of <see cref="TriggerKey" /> objects.  Must not be null
        /// </param>
        /// <returns>Whether there are more misfired triggers left to find beyond the given count.</returns>
        public virtual async Task<bool> HasMisfiredTriggersInStateAsync(ConnectionAndTransactionHolder conn, string state1, DateTimeOffset ts, int count, IList<TriggerKey> resultList)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(GetSelectNextMisfiredTriggersInStateToAcquireSql(count))))
            {
                AddCommandParameter(cmd, "nextFireTime", GetDbDateTimeValue(ts));
                AddCommandParameter(cmd, "state1", state1);

                DbDataReader rs;
                using (rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    bool hasReachedLimit = false;
                    while (await rs.ReadAsync().ConfigureAwait(false) && hasReachedLimit == false)
                    {
                        if (resultList.Count == count)
                        {
                            hasReachedLimit = true;
                        }
                        else
                        {
                            string triggerName = rs.GetString(ColumnTriggerName);
                            string groupName = rs.GetString(ColumnTriggerGroup);
                            resultList.Add(new TriggerKey(triggerName, groupName));
                        }
                    }
                    return hasReachedLimit;
                }
            }
        }

        protected virtual string GetSelectNextMisfiredTriggersInStateToAcquireSql(int count)
        {
            // by default we don't support limits, this is db specific
            return SqlSelectHasMisfiredTriggersInState;
        }

        /// <summary>
        /// Get the number of triggers in the given state that have
        /// misfired - according to the given timestamp.
        /// </summary>
        /// <param name="conn"></param>
        /// <param name="state1"></param>
        /// <param name="ts"></param>
        /// <returns></returns>
        public virtual async Task<int> CountMisfiredTriggersInStateAsync(ConnectionAndTransactionHolder conn, string state1, DateTimeOffset ts)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlCountMisfiredTriggersInStates)))
            {
                AddCommandParameter(cmd, "nextFireTime", GetDbDateTimeValue(ts));
                AddCommandParameter(cmd, "state1", state1);
                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    if (await rs.ReadAsync().ConfigureAwait(false))
                    {
                        return Convert.ToInt32(rs.GetValue(0), CultureInfo.InvariantCulture);
                    }
                }

                throw new Exception("No misfired trigger count returned.");
            }
        }

        /// <summary>
        /// Get the names of all of the triggers in the given group and state that
        /// have misfired.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="groupName">Name of the group.</param>
        /// <param name="state">The state.</param>
        /// <param name="ts">The timestamp.</param>
        /// <returns>an array of <see cref="TriggerKey" /> objects</returns>
        public virtual async Task<IReadOnlyList<TriggerKey>> SelectMisfiredTriggersInGroupInStateAsync(ConnectionAndTransactionHolder conn, string groupName, string state, DateTimeOffset ts)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectMisfiredTriggersInGroupInState))
                )
            {
                AddCommandParameter(cmd, "timestamp", GetDbDateTimeValue(ts));
                AddCommandParameter(cmd, "triggerGroup", groupName);
                AddCommandParameter(cmd, "state", state);

                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    List<TriggerKey> list = new List<TriggerKey>();
                    while (await rs.ReadAsync().ConfigureAwait(false))
                    {
                        string triggerName = rs.GetString(ColumnTriggerName);
                        list.Add(new TriggerKey(triggerName, groupName));
                    }
                    return list;
                }
            }
        }

        /// <summary>
        /// Select all of the triggers for jobs that are requesting recovery. The
        /// returned trigger objects will have unique "recoverXXX" trigger names and
        /// will be in the <see cref="SchedulerConstants.DefaultRecoveryGroup" />
        /// trigger group.
        /// </summary>
        /// <remarks>
        /// In order to preserve the ordering of the triggers, the fire time will be
        /// set from the <i>ColumnFiredTime</i> column in the <i>TableFiredTriggers</i>
        /// table. The caller is responsible for calling <see cref="IOperableTrigger.ComputeFirstFireTimeUtc" />
        /// on each returned trigger. It is also up to the caller to insert the
        /// returned triggers to ensure that they are fired.
        /// </remarks>
        /// <param name="conn">The DB Connection</param>
        /// <returns> an array of <see cref="ITrigger" /> objects</returns>
        public virtual async Task<IReadOnlyList<IOperableTrigger>> SelectTriggersForRecoveringJobsAsync(ConnectionAndTransactionHolder conn)
        {
            List<IOperableTrigger> triggers = new List<IOperableTrigger>();
            List<FiredTriggerRecord> triggerData = new List<FiredTriggerRecord>();
            List<TriggerKey> keys = new List<TriggerKey>();

            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectInstancesRecoverableFiredTriggers)))
            {
                AddCommandParameter(cmd, "instanceName", instanceId);
                AddCommandParameter(cmd, "requestsRecovery", GetDbBooleanValue(true));

                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    long dumId = SystemTime.UtcNow().Ticks;

                    while (await rs.ReadAsync().ConfigureAwait(false))
                    {
                        string jobName = rs.GetString(ColumnJobName);
                        string jobGroup = rs.GetString(ColumnJobGroup);
                        string trigName = rs.GetString(ColumnTriggerName);
                        string trigGroup = rs.GetString(ColumnTriggerGroup);
                        int priority = Convert.ToInt32(rs[ColumnPriority], CultureInfo.InvariantCulture);
                        DateTimeOffset firedTime = GetDateTimeFromDbValue(rs[ColumnFiredTime]) ?? DateTimeOffset.MinValue;
                        DateTimeOffset scheduledTime = GetDateTimeFromDbValue(rs[ColumnScheduledTime]) ?? DateTimeOffset.MinValue;
                        SimpleTriggerImpl rcvryTrig = new SimpleTriggerImpl("recover_" + instanceId + "_" + Convert.ToString(dumId++, CultureInfo.InvariantCulture),
                            SchedulerConstants.DefaultRecoveryGroup, scheduledTime);
                        rcvryTrig.JobName = jobName;
                        rcvryTrig.JobGroup = jobGroup;
                        rcvryTrig.Priority = priority;
                        rcvryTrig.MisfireInstruction = MisfireInstruction.IgnoreMisfirePolicy;

                        var dataHolder = new FiredTriggerRecord
                        {
                            ScheduleTimestamp = scheduledTime,
                            FireTimestamp = firedTime
                        };

                        triggerData.Add(dataHolder);
                        triggers.Add(rcvryTrig);
                        keys.Add(new TriggerKey(trigName, trigGroup));
                    }
                }
            }

            // read JobDataMaps with different reader..
            for (int i = 0; i < triggers.Count; i++)
            {
                IOperableTrigger trigger = triggers[i];
                TriggerKey key = keys[i];
                FiredTriggerRecord dataHolder = triggerData[i];

                // load job data map and transfer information
                JobDataMap jd = await SelectTriggerJobDataMapAsync(conn, key).ConfigureAwait(false);
                jd.Put(SchedulerConstants.FailedJobOriginalTriggerName, key.Name);
                jd.Put(SchedulerConstants.FailedJobOriginalTriggerGroup, key.Group);
                jd.Put(SchedulerConstants.FailedJobOriginalTriggerFiretime, Convert.ToString(dataHolder.FireTimestamp, CultureInfo.InvariantCulture));
                jd.Put(SchedulerConstants.FailedJobOriginalTriggerScheduledFiretime, Convert.ToString(dataHolder.ScheduleTimestamp, CultureInfo.InvariantCulture));
                trigger.JobDataMap = jd;
            }

            return triggers;
        }

        /// <summary>
        /// Delete all fired triggers.
        /// </summary>
        /// <param name="conn">The DB Connection.</param>
        /// <returns>The number of rows deleted.</returns>
        public virtual async Task<int> DeleteFiredTriggersAsync(ConnectionAndTransactionHolder conn)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlDeleteFiredTriggers)))
            {
                return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Delete all fired triggers of the given instance.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="instanceName">The instance id.</param>
        /// <returns>The number of rows deleted</returns>
        public virtual async Task<int> DeleteFiredTriggersAsync(ConnectionAndTransactionHolder conn, string instanceName)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlDeleteInstancesFiredTriggers)))
            {
                AddCommandParameter(cmd, "instanceName", instanceName);
                return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Clear (delete!) all scheduling data - all <see cref="IJob"/>s, <see cref="ITrigger" />s
        /// <see cref="ICalendar" />s.
        /// </summary>
        /// <remarks>
        /// </remarks>
        public virtual async Task ClearDataAsync(ConnectionAndTransactionHolder conn)
        {
            DbCommand ps = PrepareCommand(conn, ReplaceTablePrefix(SqlDeleteAllSimpleTriggers));
            await ps.ExecuteNonQueryAsync().ConfigureAwait(false);
            ps = PrepareCommand(conn, ReplaceTablePrefix(SqlDeleteAllSimpropTriggers));
            await ps.ExecuteNonQueryAsync().ConfigureAwait(false);
            ps = PrepareCommand(conn, ReplaceTablePrefix(SqlDeleteAllCronTriggers));
            await ps.ExecuteNonQueryAsync().ConfigureAwait(false);
            ps = PrepareCommand(conn, ReplaceTablePrefix(SqlDeleteAllBlobTriggers));
            await ps.ExecuteNonQueryAsync().ConfigureAwait(false);
            ps = PrepareCommand(conn, ReplaceTablePrefix(SqlDeleteAllTriggers));
            await ps.ExecuteNonQueryAsync().ConfigureAwait(false);
            ps = PrepareCommand(conn, ReplaceTablePrefix(SqlDeleteAllJobDetails));
            await ps.ExecuteNonQueryAsync().ConfigureAwait(false);
            ps = PrepareCommand(conn, ReplaceTablePrefix(SqlDeleteAllCalendars));
            await ps.ExecuteNonQueryAsync().ConfigureAwait(false);
            ps = PrepareCommand(conn, ReplaceTablePrefix(SqlDeleteAllPausedTriggerGrps));
            await ps.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        //---------------------------------------------------------------------------
        // jobs
        //---------------------------------------------------------------------------

        /// <summary>
        /// Insert the job detail record.
        /// </summary>
        /// <param name="conn">The DB Connection.</param>
        /// <param name="job">The job to insert.</param>
        /// <returns>Number of rows inserted.</returns>
        public virtual async Task<int> InsertJobDetailAsync(ConnectionAndTransactionHolder conn, IJobDetail job)
        {
            byte[] baos = null;
            if (job.JobDataMap.Count > 0)
            {
                baos = SerializeJobData(job.JobDataMap);
            }

            int insertResult;

            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlInsertJobDetail)))
            {
                AddCommandParameter(cmd, "jobName", job.Key.Name);
                AddCommandParameter(cmd, "jobGroup", job.Key.Group);
                AddCommandParameter(cmd, "jobDescription", job.Description);
                AddCommandParameter(cmd, "jobType", GetStorableJobTypeName(job.JobType));
                AddCommandParameter(cmd, "jobDurable", GetDbBooleanValue(job.Durable));
                AddCommandParameter(cmd, "jobVolatile", GetDbBooleanValue(job.ConcurrentExecutionDisallowed));
                AddCommandParameter(cmd, "jobStateful", GetDbBooleanValue(job.PersistJobDataAfterExecution));
                AddCommandParameter(cmd, "jobRequestsRecovery", GetDbBooleanValue(job.RequestsRecovery));

                string paramName = "jobDataMap";
                if (baos != null)
                {
                    AddCommandParameter(cmd, paramName, baos, dbProvider.Metadata.DbBinaryType);
                }
                else
                {
                    AddCommandParameter(cmd, paramName, null, dbProvider.Metadata.DbBinaryType);
                }

                insertResult = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            return insertResult;
        }

        /// <summary>
        /// Gets the db presentation for boolean value. Subclasses can overwrite this behaviour.
        /// </summary>
        /// <param name="booleanValue">Value to map to database.</param>
        /// <returns></returns>
        public virtual object GetDbBooleanValue(bool booleanValue)
        {
            // works nicely for databases we have currently supported
            return booleanValue;
        }

        /// <summary>
        /// Gets the boolean value from db presentation. Subclasses can overwrite this behaviour.
        /// </summary>
        /// <param name="columnValue">Value to map from database.</param>
        /// <returns></returns>
        public virtual bool GetBooleanFromDbValue(object columnValue)
        {
            if (columnValue != null && columnValue != DBNull.Value)
            {
                return Convert.ToBoolean(columnValue);
            }

            throw new ArgumentException("Value must be non-null.");
        }

        /// <summary>
        /// Gets the db presentation for date/time value. Subclasses can overwrite this behaviour.
        /// </summary>
        /// <param name="dateTimeValue">Value to map to database.</param>
        /// <returns></returns>
        public virtual object GetDbDateTimeValue(DateTimeOffset? dateTimeValue)
        {
            return dateTimeValue?.UtcTicks;
        }

        /// <summary>
        /// Gets the date/time value from db presentation. Subclasses can overwrite this behaviour.
        /// </summary>
        /// <param name="columnValue">Value to map from database.</param>
        /// <returns></returns>
        public virtual DateTimeOffset? GetDateTimeFromDbValue(object columnValue)
        {
            if (columnValue != null && columnValue != DBNull.Value)
            {
                var ticks = Convert.ToInt64(columnValue, CultureInfo.CurrentCulture);
                if (ticks > 0)
                {
                    return new DateTimeOffset(ticks, TimeSpan.Zero);
                }
            }
            return null;
        }

        /// <summary>
        /// Gets the db presentation for time span value. Subclasses can overwrite this behaviour.
        /// </summary>
        /// <param name="timeSpanValue">Value to map to database.</param>
        /// <returns></returns>
        public virtual object GetDbTimeSpanValue(TimeSpan? timeSpanValue)
        {
            return timeSpanValue != null ? (long?) timeSpanValue.Value.TotalMilliseconds : null;
        }

        /// <summary>
        /// Gets the time span value from db presentation. Subclasses can overwrite this behaviour.
        /// </summary>
        /// <param name="columnValue">Value to map from database.</param>
        /// <returns></returns>
        public virtual TimeSpan? GetTimeSpanFromDbValue(object columnValue)
        {
            if (columnValue != null && columnValue != DBNull.Value)
            {
                var millis = Convert.ToInt64(columnValue, CultureInfo.CurrentCulture);
                if (millis > 0)
                {
                    return TimeSpan.FromMilliseconds(millis);
                }
            }

            return null;
        }

        protected virtual string GetStorableJobTypeName(Type jobType)
        {
            if (jobType.AssemblyQualifiedName == null)
            {
                throw new ArgumentException("Cannot determine job type name when type's AssemblyQualifiedName is null");
            }

            return jobType.AssemblyQualifiedNameWithoutVersion();
        }

        /// <summary>
        /// Update the job detail record.
        /// </summary>
        /// <param name="conn">The DB Connection.</param>
        /// <param name="job">The job to update.</param>
        /// <returns>Number of rows updated.</returns>
        public virtual async Task<int> UpdateJobDetailAsync(ConnectionAndTransactionHolder conn, IJobDetail job)
        {
            byte[] baos = SerializeJobData(job.JobDataMap);

            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlUpdateJobDetail)))
            {
                AddCommandParameter(cmd, "jobDescription", job.Description);
                AddCommandParameter(cmd, "jobType", GetStorableJobTypeName(job.JobType));
                AddCommandParameter(cmd, "jobDurable", GetDbBooleanValue(job.Durable));
                AddCommandParameter(cmd, "jobVolatile", GetDbBooleanValue(job.ConcurrentExecutionDisallowed));
                AddCommandParameter(cmd, "jobStateful", GetDbBooleanValue(job.PersistJobDataAfterExecution));
                AddCommandParameter(cmd, "jobRequestsRecovery", GetDbBooleanValue(job.RequestsRecovery));
                AddCommandParameter(cmd, "jobDataMap", baos, dbProvider.Metadata.DbBinaryType);
                AddCommandParameter(cmd, "jobName", job.Key.Name);
                AddCommandParameter(cmd, "jobGroup", job.Key.Group);

                int insertResult = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                return insertResult;
            }
        }

        /// <summary>
        /// Get the names of all of the triggers associated with the given job.
        /// </summary>
        /// <param name="conn">The DB Connection.</param>
        /// <param name="jobKey">The key identifying the job.</param>
        /// <returns>An array of <see cref="TriggerKey" /> objects</returns>
        public virtual async Task<IReadOnlyList<TriggerKey>> SelectTriggerNamesForJobAsync(ConnectionAndTransactionHolder conn, JobKey jobKey)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectTriggersForJob)))
            {
                AddCommandParameter(cmd, "jobName", jobKey.Name);
                AddCommandParameter(cmd, "jobGroup", jobKey.Group);
                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    List<TriggerKey> list = new List<TriggerKey>(10);
                    while (await rs.ReadAsync().ConfigureAwait(false))
                    {
                        string trigName = rs.GetString(ColumnTriggerName);
                        string trigGroup = rs.GetString(ColumnTriggerGroup);
                        list.Add(new TriggerKey(trigName, trigGroup));
                    }
                    return list;
                }
            }
        }

        /// <summary>
        /// Delete the job detail record for the given job.
        /// </summary>
        /// <param name="conn">the DB Connection</param>
        /// <param name="jobKey">The key identifying the job.</param>
        /// <returns>the number of rows deleted</returns>
        public virtual async Task<int> DeleteJobDetailAsync(ConnectionAndTransactionHolder conn, JobKey jobKey)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlDeleteJobDetail)))
            {
                if (logger.IsDebugEnabled())
                {
                    logger.Debug("Deleting job: " + jobKey);
                }
                AddCommandParameter(cmd, "jobName", jobKey.Name);
                AddCommandParameter(cmd, "jobGroup", jobKey.Group);
                return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Check whether or not the given job is stateful.
        /// </summary>
        /// <param name="conn">the DB Connection</param>
        /// <param name="jobKey">The key identifying the job.</param>
        /// <returns>
        /// true if the job exists and is stateful, false otherwise
        /// </returns>
        public virtual async Task<bool> IsJobStatefulAsync(ConnectionAndTransactionHolder conn, JobKey jobKey)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectJobNonConcurrent)))
            {
                AddCommandParameter(cmd, "jobName", jobKey.Name);
                AddCommandParameter(cmd, "jobGroup", jobKey.Group);

                object o = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                if (o != null)
                {
                    return (bool) o;
                }

                return false;
            }
        }

        /// <summary>
        /// Check whether or not the given job exists.
        /// </summary>
        /// <param name="conn">the DB Connection</param>
        /// <param name="jobKey">The key identifying the job.</param>
        /// <returns>true if the job exists, false otherwise</returns>
        public virtual async Task<bool> JobExistsAsync(ConnectionAndTransactionHolder conn, JobKey jobKey)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectJobExistence)))
            {
                AddCommandParameter(cmd, "jobName", jobKey.Name);
                AddCommandParameter(cmd, "jobGroup", jobKey.Group);
                using (var dr = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    if (await dr.ReadAsync().ConfigureAwait(false))
                    {
                        return true;
                    }

                    return false;
                }
            }
        }

        /// <summary>
        /// Update the job data map for the given job.
        /// </summary>
        /// <param name="conn">The conn.</param>
        /// <param name="job">the job to update</param>
        /// <returns>the number of rows updated</returns>
        public virtual async Task<int> UpdateJobDataAsync(ConnectionAndTransactionHolder conn, IJobDetail job)
        {
            byte[] baos = SerializeJobData(job.JobDataMap);

            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlUpdateJobData)))
            {
                AddCommandParameter(cmd, "jobDataMap", baos, dbProvider.Metadata.DbBinaryType);
                AddCommandParameter(cmd, "jobName", job.Key.Name);
                AddCommandParameter(cmd, "jobGroup", job.Key.Group);

                return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Select the JobDetail object for a given job name / group name.
        /// </summary>
        /// <param name="conn">The DB Connection.</param>
        /// <param name="jobKey">The key identifying the job.</param>
        /// <param name="loadHelper">The load helper.</param>
        /// <returns>The populated JobDetail object.</returns>
        public virtual async Task<IJobDetail> SelectJobDetailAsync(ConnectionAndTransactionHolder conn, JobKey jobKey, ITypeLoadHelper loadHelper)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectJobDetail)))
            {
                AddCommandParameter(cmd, "jobName", jobKey.Name);
                AddCommandParameter(cmd, "jobGroup", jobKey.Group);
                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    JobDetailImpl job = null;

                    if (await rs.ReadAsync().ConfigureAwait(false))
                    {
                        job = new JobDetailImpl();

                        job.Name = rs.GetString(ColumnJobName);
                        job.Group = rs.GetString(ColumnJobGroup);
                        job.Description = rs.GetString(ColumnDescription);
                        job.JobType = loadHelper.LoadType(rs.GetString(ColumnJobClass));
                        job.Durable = GetBooleanFromDbValue(rs[ColumnIsDurable]);
                        job.RequestsRecovery = GetBooleanFromDbValue(rs[ColumnRequestsRecovery]);

                        IDictionary map = await ReadMapFromReader(rs, 6).ConfigureAwait(false);

                        if (map != null)
                        {
                            job.JobDataMap = map as JobDataMap ?? new JobDataMap(map);
                        }
                    }

                    return job;
                }
            }
        }

        private Task<IDictionary> ReadMapFromReader(DbDataReader rs, int colIndex)
        {
            if (CanUseProperties)
            {
                try
                {
                    return GetMapFromProperties(rs, colIndex);
                }
                catch (InvalidCastException)
                {
                    // old data from user error?
                    try
                    {
                        return GetObjectFromBlob<IDictionary>(rs, colIndex);
                    }
                    catch
                    {
                    }

                    // throw original exception
                    throw;
                }
            }
            else
            {
                try
                {
                    return GetObjectFromBlob<IDictionary>(rs, colIndex);
                }
                catch (InvalidCastException)
                {
                    // old data from user error?
                    try
                    {
                        // we use this then
                        return GetMapFromProperties(rs, colIndex);
                    }
                    catch
                    {
                    }

                    // throw original exception
                    throw;
                }
            }
        }

        /// <summary> build Map from java.util.Properties encoding.</summary>
        private async Task<IDictionary> GetMapFromProperties(DbDataReader rs, int idx)
        {
            NameValueCollection properties = await GetJobDataFromBlob<NameValueCollection>(rs, idx).ConfigureAwait(false);
            if (properties == null)
            {
                return null;
            }
            IDictionary map = ConvertFromProperty(properties);
            return map;
        }

        /// <summary>
        /// Select the total number of jobs stored.
        /// </summary>
        /// <param name="conn">The DB Connection.</param>
        /// <returns>The total number of jobs stored.</returns>
        public virtual async Task<int> SelectNumJobsAsync(ConnectionAndTransactionHolder conn)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectNumJobs)))
            {
                return (int) await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Select all of the job group names that are stored.
        /// </summary>
        /// <param name="conn">The DB Connection.</param>
        /// <returns>An array of <see cref="String" /> group names.</returns>
        public virtual async Task<IReadOnlyList<string>> SelectJobGroupsAsync(ConnectionAndTransactionHolder conn)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectJobGroups)))
            {
                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    List<string> list = new List<string>();
                    while (await rs.ReadAsync().ConfigureAwait(false))
                    {
                        list.Add(rs.GetString(0));
                    }

                    return list;
                }
            }
        }

        /// <summary>
        /// Select all of the jobs contained in a given group.
        /// </summary>
        /// <param name="conn">The DB Connection.</param>
        /// <param name="matcher"></param>
        /// <returns>An array of <see cref="String" /> job names.</returns>
        public virtual async Task<ISet<JobKey>> SelectJobsInGroupAsync(ConnectionAndTransactionHolder conn, GroupMatcher<JobKey> matcher)
        {
            string sql;
            string parameter;
            if (IsMatcherEquals(matcher))
            {
                sql = ReplaceTablePrefix(SqlSelectJobsInGroup);
                parameter = ToSqlEqualsClause(matcher);
            }
            else
            {
                sql = ReplaceTablePrefix(SqlSelectJobsInGroupLike);
                parameter = ToSqlLikeClause(matcher);
            }

            using (var cmd = PrepareCommand(conn, sql))
            {
                AddCommandParameter(cmd, "jobGroup", parameter);

                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    var list = new HashSet<JobKey>();
                    while (await rs.ReadAsync().ConfigureAwait(false))
                    {
                        list.Add(new JobKey(rs.GetString(0), rs.GetString(1)));
                    }

                    return list;
                }
            }
        }

        protected bool IsMatcherEquals<T>(GroupMatcher<T> matcher) where T : Key<T>
        {
            return matcher.CompareWithOperator.Equals(StringOperator.Equality);
        }

        protected string ToSqlEqualsClause<T>(GroupMatcher<T> matcher) where T : Key<T>
        {
            return matcher.CompareToValue;
        }

        protected virtual string ToSqlLikeClause<T>(GroupMatcher<T> matcher) where T : Key<T>
        {
            string groupName;
            if (StringOperator.Equality.Equals(matcher.CompareWithOperator))
            {
                groupName = matcher.CompareToValue;
            }
            else if (StringOperator.Contains.Equals(matcher.CompareWithOperator))
            {
                groupName = "%" + matcher.CompareToValue + "%";
            }
            else if (StringOperator.EndsWith.Equals(matcher.CompareWithOperator))
            {
                groupName = "%" + matcher.CompareToValue;
            }
            else if (StringOperator.StartsWith.Equals(matcher.CompareWithOperator))
            {
                groupName = matcher.CompareToValue + "%";
            }
            else if (StringOperator.Anything.Equals(matcher.CompareWithOperator))
            {
                groupName = "%";
            }
            else
            {
                throw new ArgumentOutOfRangeException("Don't know how to translate " + matcher.CompareWithOperator + " into SQL");
            }
            return groupName;
        }

        //---------------------------------------------------------------------------
        // triggers
        //---------------------------------------------------------------------------

        /// <summary>
        /// Insert the base trigger data.
        /// </summary>
        /// <param name="conn">the DB Connection</param>
        /// <param name="trigger">the trigger to insert</param>
        /// <param name="state">the state that the trigger should be stored in</param>
        /// <param name="jobDetail">The job detail.</param>
        /// <returns>the number of rows inserted</returns>
        public virtual async Task<int> InsertTriggerAsync(ConnectionAndTransactionHolder conn, IOperableTrigger trigger, string state, IJobDetail jobDetail)
        {
            byte[] baos = null;
            if (trigger.JobDataMap.Count > 0)
            {
                baos = SerializeJobData(trigger.JobDataMap);
            }

            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlInsertTrigger)))
            {
                AddCommandParameter(cmd, "triggerName", trigger.Key.Name);
                AddCommandParameter(cmd, "triggerGroup", trigger.Key.Group);
                AddCommandParameter(cmd, "triggerJobName", trigger.JobKey.Name);
                AddCommandParameter(cmd, "triggerJobGroup", trigger.JobKey.Group);
                AddCommandParameter(cmd, "triggerDescription", trigger.Description);
                AddCommandParameter(cmd, "triggerNextFireTime", GetDbDateTimeValue(trigger.GetNextFireTimeUtc()));
                AddCommandParameter(cmd, "triggerPreviousFireTime", GetDbDateTimeValue(trigger.GetPreviousFireTimeUtc()));
                AddCommandParameter(cmd, "triggerState", state);
                string paramName = "triggerType";

                ITriggerPersistenceDelegate tDel = FindTriggerPersistenceDelegate(trigger);
                string type = TriggerTypeBlob;
                if (tDel != null)
                {
                    type = tDel.GetHandledTriggerTypeDiscriminator();
                }
                AddCommandParameter(cmd, paramName, type);
                AddCommandParameter(cmd, "triggerStartTime", GetDbDateTimeValue(trigger.StartTimeUtc));
                AddCommandParameter(cmd, "triggerEndTime", GetDbDateTimeValue(trigger.EndTimeUtc));
                AddCommandParameter(cmd, "triggerCalendarName", trigger.CalendarName);
                AddCommandParameter(cmd, "triggerMisfireInstruction", trigger.MisfireInstruction);

                paramName = "triggerJobJobDataMap";
                if (baos != null)
                {
                    AddCommandParameter(cmd, paramName, baos, dbProvider.Metadata.DbBinaryType);
                }
                else
                {
                    AddCommandParameter(cmd, paramName, null, dbProvider.Metadata.DbBinaryType);
                }
                AddCommandParameter(cmd, "triggerPriority", trigger.Priority);

                int insertResult = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                if (tDel == null)
                {
                    await InsertBlobTriggerAsync(conn, trigger).ConfigureAwait(false);
                }
                else
                {
                    await tDel.InsertExtendedTriggerProperties(conn, trigger, state, jobDetail).ConfigureAwait(false);
                }

                return insertResult;
            }
        }

        /// <summary>
        /// Insert the blob trigger data.
        /// </summary>
        /// <param name="conn">The DB Connection.</param>
        /// <param name="trigger">The trigger to insert.</param>
        /// <returns>The number of rows inserted.</returns>
        public virtual async Task<int> InsertBlobTriggerAsync(ConnectionAndTransactionHolder conn, IOperableTrigger trigger)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlInsertBlobTrigger)))
            {
                // update the blob
                byte[] buf = SerializeObject(trigger);
                AddCommandParameter(cmd, "triggerName", trigger.Key.Name);
                AddCommandParameter(cmd, "triggerGroup", trigger.Key.Group);
                AddCommandParameter(cmd, "blob", buf, dbProvider.Metadata.DbBinaryType);

                return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Update the base trigger data.
        /// </summary>
        /// <param name="conn">The DB Connection.</param>
        /// <param name="trigger">The trigger to insert.</param>
        /// <param name="state">The state that the trigger should be stored in.</param>
        /// <param name="jobDetail">The job detail.</param>
        /// <returns>The number of rows updated.</returns>
        public virtual async Task<int> UpdateTriggerAsync(ConnectionAndTransactionHolder conn, IOperableTrigger trigger, string state, IJobDetail jobDetail)
        {
            // save some clock cycles by unnecessarily writing job data blob ...
            bool updateJobData = trigger.JobDataMap.Dirty;
            byte[] baos = null;
            if (updateJobData && trigger.JobDataMap.Count > 0)
            {
                baos = SerializeJobData(trigger.JobDataMap);
            }

            DbCommand cmd;

            if (updateJobData)
            {
                cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlUpdateTrigger));
            }
            else
            {
                cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlUpdateTriggerSkipData));
            }

            AddCommandParameter(cmd, "triggerJobName", trigger.JobKey.Name);
            AddCommandParameter(cmd, "triggerJobGroup", trigger.JobKey.Group);
            AddCommandParameter(cmd, "triggerDescription", trigger.Description);
            AddCommandParameter(cmd, "triggerNextFireTime", GetDbDateTimeValue(trigger.GetNextFireTimeUtc()));
            AddCommandParameter(cmd, "triggerPreviousFireTime", GetDbDateTimeValue(trigger.GetPreviousFireTimeUtc()));

            AddCommandParameter(cmd, "triggerState", state);

            ITriggerPersistenceDelegate tDel = FindTriggerPersistenceDelegate(trigger);

            string type = TriggerTypeBlob;
            if (tDel != null)
            {
                type = tDel.GetHandledTriggerTypeDiscriminator();
            }
            AddCommandParameter(cmd, "triggerType", type);

            AddCommandParameter(cmd, "triggerStartTime", GetDbDateTimeValue(trigger.StartTimeUtc));
            AddCommandParameter(cmd, "triggerEndTime", GetDbDateTimeValue(trigger.EndTimeUtc));
            AddCommandParameter(cmd, "triggerCalendarName", trigger.CalendarName);
            AddCommandParameter(cmd, "triggerMisfireInstruction", trigger.MisfireInstruction);
            AddCommandParameter(cmd, "triggerPriority", trigger.Priority);

            const string JobDataMapParameter = "triggerJobJobDataMap";
            if (updateJobData)
            {
                if (baos != null)
                {
                    AddCommandParameter(cmd, JobDataMapParameter, baos, dbProvider.Metadata.DbBinaryType);
                }
                else
                {
                    AddCommandParameter(cmd, JobDataMapParameter, null, dbProvider.Metadata.DbBinaryType);
                }
                AddCommandParameter(cmd, "triggerName", trigger.Key.Name);
                AddCommandParameter(cmd, "triggerGroup", trigger.Key.Group);
            }
            else
            {
                AddCommandParameter(cmd, "triggerName", trigger.Key.Name);
                AddCommandParameter(cmd, "triggerGroup", trigger.Key.Group);
            }

            int insertResult = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

            if (tDel == null)
            {
                await UpdateBlobTriggerAsync(conn, trigger).ConfigureAwait(false);
            }
            else
            {
                await tDel.UpdateExtendedTriggerProperties(conn, trigger, state, jobDetail).ConfigureAwait(false);
            }

            return insertResult;
        }

        /// <summary>
        /// Update the blob trigger data.
        /// </summary>
        /// <param name="conn">The DB Connection.</param>
        /// <param name="trigger">The trigger to insert.</param>
        /// <returns>The number of rows updated.</returns>
        public virtual async Task<int> UpdateBlobTriggerAsync(ConnectionAndTransactionHolder conn, IOperableTrigger trigger)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlUpdateBlobTrigger)))
            {
                // update the blob
                byte[] os = SerializeObject(trigger);

                AddCommandParameter(cmd, "blob", os, dbProvider.Metadata.DbBinaryType);
                AddCommandParameter(cmd, "triggerName", trigger.Key.Name);
                AddCommandParameter(cmd, "triggerGroup", trigger.Key.Group);

                return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Check whether or not a trigger exists.
        /// </summary>
        /// <param name="conn">The DB Connection.</param>
        /// <param name="triggerKey">the key of the trigger</param>
        /// <returns>true if the trigger exists, false otherwise</returns>
        public virtual async Task<bool> TriggerExistsAsync(ConnectionAndTransactionHolder conn, TriggerKey triggerKey)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectTriggerExistence)))
            {
                AddCommandParameter(cmd, "triggerName", triggerKey.Name);
                AddCommandParameter(cmd, "triggerGroup", triggerKey.Group);

                using (var dr = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    if (await dr.ReadAsync().ConfigureAwait(false))
                    {
                        return true;
                    }
                    return false;
                }
            }
        }

        /// <summary>
        /// Update the state for a given trigger.
        /// </summary>
        /// <param name="conn">The DB Connection.</param>
        /// <param name="triggerKey">the key of the trigger</param>
        /// <param name="state">The new state for the trigger.</param>
        /// <returns>The number of rows updated.</returns>
        public virtual async Task<int> UpdateTriggerStateAsync(ConnectionAndTransactionHolder conn, TriggerKey triggerKey, string state)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlUpdateTriggerState)))
            {
                AddCommandParameter(cmd, "state", state);
                AddCommandParameter(cmd, "triggerName", triggerKey.Name);
                AddCommandParameter(cmd, "triggerGroup", triggerKey.Group);

                return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Update the given trigger to the given new state, if it is one of the
        /// given old states.
        /// </summary>
        /// <param name="conn">The DB connection.</param>
        /// <param name="triggerKey">the key of the trigger</param>
        /// <param name="newState">The new state for the trigger.</param>
        /// <param name="oldState1">One of the old state the trigger must be in.</param>
        /// <param name="oldState2">One of the old state the trigger must be in.</param>
        /// <param name="oldState3">One of the old state the trigger must be in.</param>
        /// <returns>The number of rows updated.</returns>
        public virtual async Task<int> UpdateTriggerStateFromOtherStatesAsync(ConnectionAndTransactionHolder conn, TriggerKey triggerKey,
            string newState, string oldState1, string oldState2,
            string oldState3)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlUpdateTriggerStateFromStates)))
            {
                AddCommandParameter(cmd, "newState", newState);
                AddCommandParameter(cmd, "triggerName", triggerKey.Name);
                AddCommandParameter(cmd, "triggerGroup", triggerKey.Group);
                AddCommandParameter(cmd, "oldState1", oldState1);
                AddCommandParameter(cmd, "oldState2", oldState2);
                AddCommandParameter(cmd, "oldState3", oldState3);

                return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Update all triggers in the given group to the given new state, if they
        /// are in one of the given old states.
        /// </summary>
        /// <param name="conn">The DB connection.</param>
        /// <param name="matcher"></param>
        /// <param name="newState">The new state for the trigger.</param>
        /// <param name="oldState1">One of the old state the trigger must be in.</param>
        /// <param name="oldState2">One of the old state the trigger must be in.</param>
        /// <param name="oldState3">One of the old state the trigger must be in.</param>
        /// <returns>The number of rows updated.</returns>
        public virtual async Task<int> UpdateTriggerGroupStateFromOtherStatesAsync(ConnectionAndTransactionHolder conn, GroupMatcher<TriggerKey> matcher, string newState, string oldState1, string oldState2, string oldState3)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlUpdateTriggerGroupStateFromStates)))
            {
                AddCommandParameter(cmd, "newState", newState);
                AddCommandParameter(cmd, "groupName", ToSqlLikeClause(matcher));
                AddCommandParameter(cmd, "oldState1", oldState1);
                AddCommandParameter(cmd, "oldState2", oldState2);
                AddCommandParameter(cmd, "oldState3", oldState3);

                return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Update the given trigger to the given new state, if it is in the given
        /// old state.
        /// </summary>
        /// <param name="conn">the DB connection</param>
        /// <param name="triggerKey">the key of the trigger</param>
        /// <param name="newState">the new state for the trigger</param>
        /// <param name="oldState">the old state the trigger must be in</param>
        /// <returns>int the number of rows updated</returns>
        public virtual async Task<int> UpdateTriggerStateFromOtherStateAsync(ConnectionAndTransactionHolder conn, TriggerKey triggerKey,
            string newState, string oldState)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlUpdateTriggerStateFromState)))
            {
                AddCommandParameter(cmd, "newState", newState);
                AddCommandParameter(cmd, "triggerName", triggerKey.Name);
                AddCommandParameter(cmd, "triggerGroup", triggerKey.Group);
                AddCommandParameter(cmd, "oldState", oldState);

                return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Update all of the triggers of the given group to the given new state, if
        /// they are in the given old state.
        /// </summary>
        /// <param name="conn">the DB connection</param>
        /// <param name="matcher"></param>
        /// <param name="newState">the new state for the trigger group</param>
        /// <param name="oldState">the old state the triggers must be in</param>
        /// <returns>int the number of rows updated</returns>
        public virtual async Task<int> UpdateTriggerGroupStateFromOtherStateAsync(ConnectionAndTransactionHolder conn, GroupMatcher<TriggerKey> matcher, string newState, string oldState)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlUpdateTriggerGroupStateFromState)))
            {
                AddCommandParameter(cmd, "newState", newState);
                AddCommandParameter(cmd, "triggerGroup", ToSqlLikeClause(matcher));
                AddCommandParameter(cmd, "oldState", oldState);

                return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Update the states of all triggers associated with the given job.
        /// </summary>
        /// <param name="conn">the DB Connection</param>
        /// <param name="jobKey">the key of the job</param>
        /// <param name="state">the new state for the triggers</param>
        /// <returns>the number of rows updated</returns>
        public virtual async Task<int> UpdateTriggerStatesForJobAsync(ConnectionAndTransactionHolder conn, JobKey jobKey, string state)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlUpdateJobTriggerStates)))
            {
                AddCommandParameter(cmd, "state", state);
                AddCommandParameter(cmd, "jobName", jobKey.Name);
                AddCommandParameter(cmd, "jobGroup", jobKey.Group);

                return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Updates the state of the trigger states for job from other.
        /// </summary>
        /// <param name="conn">The conn.</param>
        /// <param name="jobKey">Key of the job.</param>
        /// <param name="state">The state.</param>
        /// <param name="oldState">The old state.</param>
        /// <returns></returns>
        public virtual async Task<int> UpdateTriggerStatesForJobFromOtherStateAsync(ConnectionAndTransactionHolder conn, JobKey jobKey,
            string state, string oldState)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlUpdateJobTriggerStatesFromOtherState)))
            {
                AddCommandParameter(cmd, "state", state);
                AddCommandParameter(cmd, "jobName", jobKey.Name);
                AddCommandParameter(cmd, "jobGroup", jobKey.Group);
                AddCommandParameter(cmd, "oldState", oldState);

                return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Delete the cron trigger data for a trigger.
        /// </summary>
        /// <param name="conn">the DB Connection</param>
        /// <param name="triggerKey">the key of the trigger</param>
        /// <returns>the number of rows deleted</returns>
        public virtual async Task<int> DeleteBlobTriggerAsync(ConnectionAndTransactionHolder conn, TriggerKey triggerKey)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlDeleteBlobTrigger)))
            {
                AddCommandParameter(cmd, "triggerName", triggerKey.Name);
                AddCommandParameter(cmd, "triggerGroup", triggerKey.Group);

                return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Delete the base trigger data for a trigger.
        /// </summary>
        /// <param name="conn">the DB Connection</param>
        /// <param name="triggerKey">the key of the trigger</param>
        /// <returns>the number of rows deleted</returns>
        public virtual async Task<int> DeleteTriggerAsync(ConnectionAndTransactionHolder conn, TriggerKey triggerKey)
        {
            await DeleteTriggerExtensionAsync(conn, triggerKey).ConfigureAwait(false);

            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlDeleteTrigger)))
            {
                AddCommandParameter(cmd, "triggerName", triggerKey.Name);
                AddCommandParameter(cmd, "triggerGroup", triggerKey.Group);

                return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        protected virtual async Task DeleteTriggerExtensionAsync(ConnectionAndTransactionHolder conn, TriggerKey triggerKey)
        {
            foreach (ITriggerPersistenceDelegate tDel in triggerPersistenceDelegates)
            {
                if (await tDel.DeleteExtendedTriggerProperties(conn, triggerKey).ConfigureAwait(false) > 0)
                {
                    return; // as soon as one affects a row, we're done.
                }
            }

            await DeleteBlobTriggerAsync(conn, triggerKey).ConfigureAwait(false);
        }

        /// <summary>
        /// Select the number of triggers associated with a given job.
        /// </summary>
        /// <param name="conn">the DB Connection</param>
        /// <param name="jobKey">the key of the job</param>
        /// <returns>the number of triggers for the given job</returns>
        public virtual async Task<int> SelectNumTriggersForJobAsync(ConnectionAndTransactionHolder conn, JobKey jobKey)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectNumTriggersForJob)))
            {
                AddCommandParameter(cmd, "jobName", jobKey.Name);
                AddCommandParameter(cmd, "jobGroup", jobKey.Group);

                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    if (await rs.ReadAsync().ConfigureAwait(false))
                    {
                        return Convert.ToInt32(rs.GetValue(0), CultureInfo.InvariantCulture);
                    }
                    return 0;
                }
            }
        }

        public virtual Task<IJobDetail> SelectJobForTriggerAsync(ConnectionAndTransactionHolder conn, TriggerKey triggerKey, ITypeLoadHelper loadHelper)
        {
            return SelectJobForTriggerAsync(conn, triggerKey, loadHelper, true);
        }

        public virtual async Task<IJobDetail> SelectJobForTriggerAsync(ConnectionAndTransactionHolder conn, TriggerKey triggerKey, ITypeLoadHelper loadHelper, bool loadJobType)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectJobForTrigger)))
            {
                AddCommandParameter(cmd, "triggerName", triggerKey.Name);
                AddCommandParameter(cmd, "triggerGroup", triggerKey.Group);
                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    if (await rs.ReadAsync().ConfigureAwait(false))
                    {
                        JobDetailImpl job = new JobDetailImpl();
                        job.Name = rs.GetString(ColumnJobName);
                        job.Group = rs.GetString(ColumnJobGroup);
                        job.Durable = GetBooleanFromDbValue(rs[ColumnIsDurable]);
                        if (loadJobType)
                        {
                            job.JobType = loadHelper.LoadType(rs.GetString(ColumnJobClass));
                        }
                        job.RequestsRecovery = GetBooleanFromDbValue(rs[ColumnRequestsRecovery]);

                        return job;
                    }
                    if (logger.IsDebugEnabled())
                    {
                        logger.Debug("No job for trigger '" + triggerKey + "'.");
                    }
                    return null;
                }
            }
        }

        /// <summary>
        /// Select the triggers for a job
        /// </summary>
        /// <param name="conn">the DB Connection</param>
        /// <param name="jobKey">the key of the job</param>
        /// <returns>
        /// an array of <see cref="ITrigger" /> objects
        /// associated with a given job.
        /// </returns>
        public virtual async Task<IReadOnlyList<IOperableTrigger>> SelectTriggersForJobAsync(ConnectionAndTransactionHolder conn, JobKey jobKey)
        {
            List<IOperableTrigger> trigList = new List<IOperableTrigger>();
            List<TriggerKey> keys = new List<TriggerKey>();

            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectTriggersForJob)))
            {
                AddCommandParameter(cmd, "jobName", jobKey.Name);
                AddCommandParameter(cmd, "jobGroup", jobKey.Group);

                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    while (await rs.ReadAsync().ConfigureAwait(false))
                    {
                        keys.Add(new TriggerKey(rs.GetString(0), rs.GetString(1)));
                    }
                }
            }

            foreach (TriggerKey triggerKey in keys)
            {
                IOperableTrigger t = await SelectTriggerAsync(conn, triggerKey).ConfigureAwait(false);

                if (t != null)
                {
                    trigList.Add(t);
                }
            }

            return trigList;
        }

        /// <summary>
        /// Select the triggers for a calendar
        /// </summary>
        /// <param name="conn">The DB Connection.</param>
        /// <param name="calName">Name of the calendar.</param>
        /// <returns>
        /// An array of <see cref="ITrigger" /> objects associated with a given job.
        /// </returns>
        public virtual async Task<IReadOnlyList<IOperableTrigger>> SelectTriggersForCalendarAsync(ConnectionAndTransactionHolder conn, string calName)
        {
            List<TriggerKey> keys = new List<TriggerKey>();
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectTriggersForCalendar)))
            {
                AddCommandParameter(cmd, "calendarName", calName);
                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    while (await rs.ReadAsync().ConfigureAwait(false))
                    {
                        keys.Add(new TriggerKey(rs.GetString(ColumnTriggerName), rs.GetString(ColumnTriggerGroup)));
                    }
                }
            }

            List<IOperableTrigger> triggers = new List<IOperableTrigger>();
            foreach (var key in keys)
            {
                triggers.Add(await SelectTriggerAsync(conn, key).ConfigureAwait(false));
            }
            return triggers;
        }

        /// <summary>
        /// Select a trigger.
        /// </summary>
        /// <param name="conn">the DB Connection</param>
        /// <param name="triggerKey">the key of the trigger</param>
        /// <returns>The <see cref="ITrigger" /> object</returns>
        public virtual async Task<IOperableTrigger> SelectTriggerAsync(ConnectionAndTransactionHolder conn, TriggerKey triggerKey)
        {
            IOperableTrigger trigger = null;
            string triggerType;

            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectTrigger)))
            {
                AddCommandParameter(cmd, "triggerName", triggerKey.Name);
                AddCommandParameter(cmd, "triggerGroup", triggerKey.Group);

                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    if (await rs.ReadAsync().ConfigureAwait(false))
                    {
                        string jobName = rs.GetString(ColumnJobName);
                        string jobGroup = rs.GetString(ColumnJobGroup);
                        string description = rs.GetString(ColumnDescription);
                        triggerType = rs.GetString(ColumnTriggerType);
                        string calendarName = rs.GetString(ColumnCalendarName);
                        int misFireInstr = rs.GetInt32(ColumnMifireInstruction);
                        int priority = rs.GetInt32(ColumnPriority);

                        IDictionary map = await ReadMapFromReader(rs, 11).ConfigureAwait(false);

                        DateTimeOffset? nextFireTimeUtc = GetDateTimeFromDbValue(rs[ColumnNextFireTime]);
                        DateTimeOffset? previousFireTimeUtc = GetDateTimeFromDbValue(rs[ColumnPreviousFireTime]);
                        DateTimeOffset startTimeUtc = GetDateTimeFromDbValue(rs[ColumnStartTime]) ?? DateTimeOffset.MinValue;
                        DateTimeOffset? endTimeUtc = GetDateTimeFromDbValue(rs[ColumnEndTime]);

                        // done reading
                        rs.Close();

                        if (triggerType.Equals(TriggerTypeBlob))
                        {
                            using (var cmd2 = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectBlobTrigger)))
                            {
                                AddCommandParameter(cmd2, "triggerName", triggerKey.Name);
                                AddCommandParameter(cmd2, "triggerGroup", triggerKey.Group);
                                using (var rs2 = await cmd2.ExecuteReaderAsync().ConfigureAwait(false))
                                {
                                    if (await rs2.ReadAsync().ConfigureAwait(false))
                                    {
                                        trigger = await GetObjectFromBlob<IOperableTrigger>(rs2, 0).ConfigureAwait(false);
                                    }
                                }
                            }
                        }
                        else
                        {
                            ITriggerPersistenceDelegate tDel = FindTriggerPersistenceDelegate(triggerType);

                            if (tDel == null)
                            {
                                throw new JobPersistenceException("No TriggerPersistenceDelegate for trigger discriminator type: " + triggerType);
                            }

                            TriggerPropertyBundle triggerProps;
                            try
                            {
                                triggerProps = await tDel.LoadExtendedTriggerProperties(conn, triggerKey).ConfigureAwait(false);
                            }
                            catch (InvalidOperationException)
                            {
                                if (await IsTriggerStillPresent(cmd).ConfigureAwait(false))
                                {
                                    throw;
                                }
                                else
                                {
                                    // QTZ-386 Trigger has been deleted
                                    return null;
                                }
                            }

                            TriggerBuilder tb = TriggerBuilder.Create()
                                .WithDescription(description)
                                .WithPriority(priority)
                                .StartAt(startTimeUtc)
                                .EndAt(endTimeUtc)
                                .WithIdentity(triggerKey)
                                .ModifiedByCalendar(calendarName)
                                .WithSchedule(triggerProps.ScheduleBuilder)
                                .ForJob(new JobKey(jobName, jobGroup));

                            if (map != null)
                            {
                                tb.UsingJobData(map as JobDataMap ?? new JobDataMap(map));
                            }

                            trigger = (IOperableTrigger) tb.Build();

                            trigger.MisfireInstruction = misFireInstr;
                            trigger.SetNextFireTimeUtc(nextFireTimeUtc);
                            trigger.SetPreviousFireTimeUtc(previousFireTimeUtc);

                            SetTriggerStateProperties(trigger, triggerProps);
                        }
                    }
                }
            }

            return trigger;
        }

        private static async Task<bool> IsTriggerStillPresent(DbCommand command)
        {
            using (var rs = await command.ExecuteReaderAsync().ConfigureAwait(false))
            {
                return await rs.ReadAsync().ConfigureAwait(false);
            }
        }

        private static void SetTriggerStateProperties(IOperableTrigger trigger, TriggerPropertyBundle props)
        {
            if (props.StatePropertyNames == null)
            {
                return;
            }

            ObjectUtils.SetObjectProperties(trigger, props.StatePropertyNames, props.StatePropertyValues);
        }

        /// <summary>
        /// Select a trigger's JobDataMap.
        /// </summary>
        /// <param name="conn">the DB Connection</param>
        /// <param name="triggerKey">the key of the trigger</param>
        /// <returns>The <see cref="JobDataMap" /> of the Trigger, never null, but possibly empty. </returns>
        public virtual async Task<JobDataMap> SelectTriggerJobDataMapAsync(ConnectionAndTransactionHolder conn, TriggerKey triggerKey)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectTriggerData)))
            {
                AddCommandParameter(cmd, "triggerName", triggerKey.Name);
                AddCommandParameter(cmd, "triggerGroup", triggerKey.Group);

                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    if (await rs.ReadAsync().ConfigureAwait(false))
                    {
                        IDictionary map = await ReadMapFromReader(rs, 0).ConfigureAwait(false);
                        if (map != null)
                        {
                            return map as JobDataMap ?? new JobDataMap(map);
                        }
                    }
                }
            }

            return new JobDataMap();
        }

        /// <summary>
        /// Select a trigger's state value.
        /// </summary>
        /// <param name="conn">the DB Connection</param>
        /// <param name="triggerKey">the key of the trigger</param>
        /// <returns>The <see cref="ITrigger" /> object</returns>
        public virtual async Task<string> SelectTriggerStateAsync(ConnectionAndTransactionHolder conn, TriggerKey triggerKey)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectTriggerState)))
            {
                string state;

                AddCommandParameter(cmd, "triggerName", triggerKey.Name);
                AddCommandParameter(cmd, "triggerGroup", triggerKey.Group);
                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    if (await rs.ReadAsync().ConfigureAwait(false))
                    {
                        state = rs.GetString(ColumnTriggerState);
                    }
                    else
                    {
                        state = StateDeleted;
                    }
                }
                return string.Intern(state);
            }
        }

        /// <summary>
        /// Select a trigger status (state and next fire time).
        /// </summary>
        /// <param name="conn">the DB Connection</param>
        /// <param name="triggerKey">the key of the trigger</param>
        /// <returns>
        /// a <see cref="TriggerStatus" /> object, or null
        /// </returns>
        public virtual async Task<TriggerStatus> SelectTriggerStatusAsync(ConnectionAndTransactionHolder conn, TriggerKey triggerKey)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectTriggerStatus)))
            {
                TriggerStatus status = null;

                AddCommandParameter(cmd, "triggerName", triggerKey.Name);
                AddCommandParameter(cmd, "triggerGroup", triggerKey.Group);
                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    if (await rs.ReadAsync().ConfigureAwait(false))
                    {
                        string state = rs.GetString(ColumnTriggerState);
                        object nextFireTime = rs[ColumnNextFireTime];
                        string jobName = rs.GetString(ColumnJobName);
                        string jobGroup = rs.GetString(ColumnJobGroup);

                        DateTimeOffset? nft = GetDateTimeFromDbValue(nextFireTime);

                        status = new TriggerStatus(state, nft);
                        status.Key = triggerKey;
                        status.JobKey = new JobKey(jobName, jobGroup);
                    }
                }
                return status;
            }
        }

        /// <summary>
        /// Select the total number of triggers stored.
        /// </summary>
        /// <param name="conn">the DB Connection</param>
        /// <returns>the total number of triggers stored</returns>
        public virtual async Task<int> SelectNumTriggersAsync(ConnectionAndTransactionHolder conn)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectNumTriggers)))
            {
                int count = 0;

                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    if (await rs.ReadAsync().ConfigureAwait(false))
                    {
                        count = Convert.ToInt32(rs.GetInt32(0));
                    }
                }

                return count;
            }
        }

        /// <summary>
        /// Select all of the trigger group names that are stored.
        /// </summary>
        /// <param name="conn">the DB Connection</param>
        /// <returns>
        /// an array of <see cref="String" /> group names
        /// </returns>
        public virtual async Task<IReadOnlyList<string>> SelectTriggerGroupsAsync(ConnectionAndTransactionHolder conn)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectTriggerGroups)))
            {
                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    List<string> list = new List<string>();
                    while (await rs.ReadAsync().ConfigureAwait(false))
                    {
                        list.Add((string) rs[0]);
                    }

                    return list;
                }
            }
        }

        public virtual async Task<IReadOnlyList<string>> SelectTriggerGroupsAsync(ConnectionAndTransactionHolder conn, GroupMatcher<TriggerKey> matcher)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectTriggerGroupsFiltered)))
            {
                AddCommandParameter(cmd, "triggerGroup", ToSqlLikeClause(matcher));
                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    List<string> list = new List<string>();
                    while (await rs.ReadAsync().ConfigureAwait(false))
                    {
                        list.Add((string) rs[0]);
                    }

                    return list;
                }
            }
        }

        /// <summary>
        /// Select all of the triggers contained in a given group.
        /// </summary>
        /// <param name="conn">the DB Connection</param>
        /// <param name="matcher"></param>
        /// <returns>
        /// an array of <see cref="String" /> trigger names
        /// </returns>
        public virtual async Task<ISet<TriggerKey>> SelectTriggersInGroupAsync(ConnectionAndTransactionHolder conn, GroupMatcher<TriggerKey> matcher)
        {
            string sql;
            string parameter;
            if (IsMatcherEquals(matcher))
            {
                sql = ReplaceTablePrefix(SqlSelectTriggersInGroup);
                parameter = ToSqlEqualsClause(matcher);
            }
            else
            {
                sql = ReplaceTablePrefix(SqlSelectTriggersInGroupLike);
                parameter = ToSqlLikeClause(matcher);
            }

            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(sql)))
            {
                AddCommandParameter(cmd, "triggerGroup", parameter);
                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    var keys = new HashSet<TriggerKey>();
                    while (await rs.ReadAsync().ConfigureAwait(false))
                    {
                        keys.Add(new TriggerKey(rs.GetString(0), rs.GetString(1)));
                    }

                    return keys;
                }
            }
        }

        /// <summary>
        /// Inserts the paused trigger group.
        /// </summary>
        /// <param name="conn">The conn.</param>
        /// <param name="groupName">Name of the group.</param>
        /// <returns></returns>
        public virtual async Task<int> InsertPausedTriggerGroupAsync(ConnectionAndTransactionHolder conn, string groupName)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlInsertPausedTriggerGroup)))
            {
                AddCommandParameter(cmd, "triggerGroup", groupName);
                int rows = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                return rows;
            }
        }

        /// <summary>
        /// Deletes the paused trigger group.
        /// </summary>
        /// <param name="conn">The conn.</param>
        /// <param name="groupName">Name of the group.</param>
        /// <returns></returns>
        public virtual async Task<int> DeletePausedTriggerGroupAsync(ConnectionAndTransactionHolder conn, string groupName)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlDeletePausedTriggerGroup)))
            {
                AddCommandParameter(cmd, "triggerGroup", groupName);
                int rows = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                return rows;
            }
        }

        public virtual async Task<int> DeletePausedTriggerGroupAsync(ConnectionAndTransactionHolder conn, GroupMatcher<TriggerKey> matcher)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlDeletePausedTriggerGroup)))
            {
                AddCommandParameter(cmd, "triggerGroup", ToSqlLikeClause(matcher));
                int rows = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                return rows;
            }
        }

        /// <summary>
        /// Deletes all paused trigger groups.
        /// </summary>
        /// <param name="conn">The conn.</param>
        /// <returns></returns>
        public virtual async Task<int> DeleteAllPausedTriggerGroupsAsync(ConnectionAndTransactionHolder conn)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlDeletePausedTriggerGroups)))
            {
                int rows = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                return rows;
            }
        }

        /// <summary>
        /// Determines whether the specified trigger group is paused.
        /// </summary>
        /// <param name="conn">The conn.</param>
        /// <param name="groupName">Name of the group.</param>
        /// <returns>
        /// 	<c>true</c> if trigger group is paused; otherwise, <c>false</c>.
        /// </returns>
        public virtual async Task<bool> IsTriggerGroupPausedAsync(ConnectionAndTransactionHolder conn, string groupName)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectPausedTriggerGroup)))
            {
                AddCommandParameter(cmd, "triggerGroup", groupName);
                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    return await rs.ReadAsync().ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Determines whether given trigger group already exists.
        /// </summary>
        /// <param name="conn">The conn.</param>
        /// <param name="groupName">Name of the group.</param>
        /// <returns>
        /// 	<c>true</c> if trigger group exists; otherwise, <c>false</c>.
        /// </returns>
        public virtual async Task<bool> IsExistingTriggerGroupAsync(ConnectionAndTransactionHolder conn, string groupName)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectNumTriggersInGroup)))
            {
                AddCommandParameter(cmd, "triggerGroup", groupName);
                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    if (!await rs.ReadAsync().ConfigureAwait(false))
                    {
                        return false;
                    }

                    return (Convert.ToInt32(rs.GetInt32(0)) > 0);
                }
            }
        }

        //---------------------------------------------------------------------------
        // calendars
        //---------------------------------------------------------------------------

        /// <summary>
        /// Insert a new calendar.
        /// </summary>
        /// <param name="conn">the DB Connection</param>
        /// <param name="calendarName">The name for the new calendar.</param>
        /// <param name="calendar">The calendar.</param>
        /// <returns>the number of rows inserted</returns>
        /// <throws>  IOException </throws>
        public virtual async Task<int> InsertCalendarAsync(ConnectionAndTransactionHolder conn, string calendarName, ICalendar calendar)
        {
            byte[] baos = SerializeObject(calendar);

            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlInsertCalendar)))
            {
                AddCommandParameter(cmd, "calendarName", calendarName);
                AddCommandParameter(cmd, "calendar", baos, dbProvider.Metadata.DbBinaryType);

                return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Update a calendar.
        /// </summary>
        /// <param name="conn">the DB Connection</param>
        /// <param name="calendarName">The name for the new calendar.</param>
        /// <param name="calendar">The calendar.</param>
        /// <returns>the number of rows updated</returns>
        /// <throws>  IOException </throws>
        public virtual async Task<int> UpdateCalendarAsync(ConnectionAndTransactionHolder conn, string calendarName, ICalendar calendar)
        {
            byte[] baos = SerializeObject(calendar);

            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlUpdateCalendar)))
            {
                AddCommandParameter(cmd, "calendar", baos, dbProvider.Metadata.DbBinaryType);
                AddCommandParameter(cmd, "calendarName", calendarName);

                return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Check whether or not a calendar exists.
        /// </summary>
        /// <param name="conn">the DB Connection</param>
        /// <param name="calendarName">The name of the calendar.</param>
        /// <returns>
        /// true if the trigger exists, false otherwise
        /// </returns>
        public virtual async Task<bool> CalendarExistsAsync(ConnectionAndTransactionHolder conn, string calendarName)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectCalendarExistence)))
            {
                AddCommandParameter(cmd, "calendarName", calendarName);
                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    if (await rs.ReadAsync().ConfigureAwait(false))
                    {
                        return true;
                    }

                    return false;
                }
            }
        }

        /// <summary>
        /// Select a calendar.
        /// </summary>
        /// <param name="conn">the DB Connection</param>
        /// <param name="calendarName">The name of the calendar.</param>
        /// <returns>the Calendar</returns>
        /// <throws>  ClassNotFoundException </throws>
        /// <throws>  IOException </throws>
        public virtual async Task<ICalendar> SelectCalendarAsync(ConnectionAndTransactionHolder conn, string calendarName)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectCalendar)))
            {
                AddCommandParameter(cmd, "calendarName", calendarName);
                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    ICalendar cal = null;
                    if (await rs.ReadAsync().ConfigureAwait(false))
                    {
                        cal = await GetObjectFromBlob<ICalendar>(rs, 0).ConfigureAwait(false);
                    }
                    if (null == cal)
                    {
                        logger.Warn("Couldn't find calendar with name '" + calendarName + "'.");
                    }
                    return cal;
                }
            }
        }

        /// <summary>
        /// Check whether or not a calendar is referenced by any triggers.
        /// </summary>
        /// <param name="conn">the DB Connection</param>
        /// <param name="calendarName">The name of the calendar.</param>
        /// <returns>
        /// true if any triggers reference the calendar, false otherwise
        /// </returns>
        public virtual async Task<bool> CalendarIsReferencedAsync(ConnectionAndTransactionHolder conn, string calendarName)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectReferencedCalendar)))
            {
                AddCommandParameter(cmd, "calendarName", calendarName);
                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    if (await rs.ReadAsync().ConfigureAwait(false))
                    {
                        return true;
                    }

                    return false;
                }
            }
        }

        /// <summary>
        /// Delete a calendar.
        /// </summary>
        /// <param name="conn">the DB Connection</param>
        /// <param name="calendarName">The name of the trigger.</param>
        /// <returns>the number of rows deleted</returns>
        public virtual async Task<int> DeleteCalendarAsync(ConnectionAndTransactionHolder conn, string calendarName)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlDeleteCalendar)))
            {
                AddCommandParameter(cmd, "calendarName", calendarName);
                return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Select the total number of calendars stored.
        /// </summary>
        /// <param name="conn">the DB Connection</param>
        /// <returns>the total number of calendars stored</returns>
        public virtual async Task<int> SelectNumCalendarsAsync(ConnectionAndTransactionHolder conn)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectNumCalendars)))
            {
                int count = 0;
                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    if (await rs.ReadAsync().ConfigureAwait(false))
                    {
                        count = Convert.ToInt32(rs.GetValue(0), CultureInfo.InvariantCulture);
                    }
                    return count;
                }
            }
        }

        /// <summary>
        /// Select all of the stored calendars.
        /// </summary>
        /// <param name="conn">the DB Connection</param>
        /// <returns>
        /// an array of <see cref="String" /> calendar names
        /// </returns>
        public virtual async Task<IReadOnlyList<string>> SelectCalendarsAsync(ConnectionAndTransactionHolder conn)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectCalendars)))
            {
                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    List<string> list = new List<string>();
                    while (await rs.ReadAsync().ConfigureAwait(false))
                    {
                        list.Add((string) rs[0]);
                    }
                    return list;
                }
            }
        }

        //---------------------------------------------------------------------------
        // trigger firing
        //---------------------------------------------------------------------------

        /// <summary>
        /// Select the trigger that will be fired at the given fire time.
        /// </summary>
        /// <param name="conn">the DB Connection</param>
        /// <param name="fireTime">the time that the trigger will be fired</param>
        /// <returns>
        /// a <see cref="TriggerKey" /> representing the
        /// trigger that will be fired at the given fire time, or null if no
        /// trigger will be fired at that time
        /// </returns>
        public virtual async Task<TriggerKey> SelectTriggerForFireTimeAsync(ConnectionAndTransactionHolder conn, DateTimeOffset fireTime)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectTriggerForFireTime)))
            {
                AddCommandParameter(cmd, "state", StateWaiting);
                AddCommandParameter(cmd, "fireTime", GetDbDateTimeValue(fireTime));

                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    if (await rs.ReadAsync().ConfigureAwait(false))
                    {
                        return new TriggerKey(rs.GetString(ColumnTriggerName), rs.GetString(ColumnTriggerGroup));
                    }

                    return null;
                }
            }
        }

        /// <summary>
        /// Select the next trigger which will fire to fire between the two given timestamps 
        /// in ascending order of fire time, and then descending by priority.
        /// </summary>
        /// <param name="conn">The conn.</param>
        /// <param name="noLaterThan">highest value of <see cref="ITrigger.GetNextFireTimeUtc" /> of the triggers (exclusive)</param>
        /// <param name="noEarlierThan">highest value of <see cref="ITrigger.GetNextFireTimeUtc" /> of the triggers (inclusive)</param>
        /// <param name="maxCount">maximum number of trigger keys allow to acquired in the returning list.</param>
        /// <returns>A (never null, possibly empty) list of the identifiers (Key objects) of the next triggers to be fired.</returns>
        public virtual async Task<IReadOnlyList<TriggerKey>> SelectTriggerToAcquireAsync(ConnectionAndTransactionHolder conn, DateTimeOffset noLaterThan, DateTimeOffset noEarlierThan, int maxCount)
        {
            if (maxCount < 1)
            {
                maxCount = 1; // we want at least one trigger back.
            }

            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(GetSelectNextTriggerToAcquireSql(maxCount))))
            {
                List<TriggerKey> nextTriggers = new List<TriggerKey>();

                AddCommandParameter(cmd, "state", StateWaiting);
                AddCommandParameter(cmd, "noLaterThan", GetDbDateTimeValue(noLaterThan));
                AddCommandParameter(cmd, "noEarlierThan", GetDbDateTimeValue(noEarlierThan));

                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    while (await rs.ReadAsync().ConfigureAwait(false) && nextTriggers.Count < maxCount)
                    {
                        nextTriggers.Add(new TriggerKey((string) rs[ColumnTriggerName], (string) rs[ColumnTriggerGroup]));
                    }
                }
                return nextTriggers;
            }
        }

        protected virtual string GetSelectNextTriggerToAcquireSql(int maxCount)
        {
            // by default we don't support limits, this is db specific
            return SqlSelectNextTriggerToAcquire;
        }

        /// <summary>
        /// Insert a fired trigger.
        /// </summary>
        /// <param name="conn">the DB Connection</param>
        /// <param name="trigger">the trigger</param>
        /// <param name="state">the state that the trigger should be stored in</param>
        /// <param name="job">The job.</param>
        /// <returns>the number of rows inserted</returns>
        public virtual async Task<int> InsertFiredTriggerAsync(ConnectionAndTransactionHolder conn, IOperableTrigger trigger, string state,
            IJobDetail job)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlInsertFiredTrigger)))
            {
                AddCommandParameter(cmd, "triggerEntryId", trigger.FireInstanceId);
                AddCommandParameter(cmd, "triggerName", trigger.Key.Name);
                AddCommandParameter(cmd, "triggerGroup", trigger.Key.Group);
                AddCommandParameter(cmd, "triggerInstanceName", instanceId);
                AddCommandParameter(cmd, "triggerFireTime", GetDbDateTimeValue(SystemTime.UtcNow()));
                AddCommandParameter(cmd, "triggerScheduledTime", GetDbDateTimeValue(trigger.GetNextFireTimeUtc()));
                AddCommandParameter(cmd, "triggerState", state);
                if (job != null)
                {
                    AddCommandParameter(cmd, "triggerJobName", trigger.JobKey.Name);
                    AddCommandParameter(cmd, "triggerJobGroup", trigger.JobKey.Group);
                    AddCommandParameter(cmd, "triggerJobStateful", GetDbBooleanValue(job.ConcurrentExecutionDisallowed));
                    AddCommandParameter(cmd, "triggerJobRequestsRecovery", GetDbBooleanValue(job.RequestsRecovery));
                }
                else
                {
                    AddCommandParameter(cmd, "triggerJobName", null);
                    AddCommandParameter(cmd, "triggerJobGroup", null);
                    AddCommandParameter(cmd, "triggerJobStateful", GetDbBooleanValue(false));
                    AddCommandParameter(cmd, "triggerJobRequestsRecovery", GetDbBooleanValue(false));
                }

                AddCommandParameter(cmd, "triggerPriority", trigger.Priority);

                return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// <para>
        /// Update a fired trigger.
        /// </para>
        /// </summary>
        /// <remarks>
        /// </remarks>
        /// <param name="conn"></param>
        /// <param name="trigger"></param>
        /// <param name="state"></param>
        /// <param name="job"></param>
        /// the DB Connection
        /// the trigger
        /// the state that the trigger should be stored in
        /// <returns>the number of rows inserted</returns>
        public virtual async Task<int> UpdateFiredTriggerAsync(ConnectionAndTransactionHolder conn, IOperableTrigger trigger, string state, IJobDetail job)
        {
            var ps = PrepareCommand(conn, ReplaceTablePrefix(SqlUpdateFiredTrigger));
            AddCommandParameter(ps, "instanceName", instanceId);
            AddCommandParameter(ps, "firedTime", GetDbDateTimeValue(SystemTime.UtcNow()));
            AddCommandParameter(ps, "scheduledTime", GetDbDateTimeValue(trigger.GetNextFireTimeUtc()));
            AddCommandParameter(ps, "entryState", state);

            if (job != null)
            {
                AddCommandParameter(ps, "jobName", trigger.JobKey.Name);
                AddCommandParameter(ps, "jobGroup", trigger.JobKey.Group);
                AddCommandParameter(ps, "isNonConcurrent", GetDbBooleanValue(job.ConcurrentExecutionDisallowed));
                AddCommandParameter(ps, "requestsRecover", GetDbBooleanValue(job.RequestsRecovery));
            }
            else
            {
                AddCommandParameter(ps, "jobName", null);
                AddCommandParameter(ps, "JobGroup", null);
                AddCommandParameter(ps, "isNonConcurrent", GetDbBooleanValue(false));
                AddCommandParameter(ps, "requestsRecover", GetDbBooleanValue(false));
            }

            AddCommandParameter(ps, "entryId", trigger.FireInstanceId);

            return await ps.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Select the states of all fired-trigger records for a given trigger, or
        /// trigger group if trigger name is <see langword="null" />.
        /// </summary>
        /// <param name="conn">The DB connection.</param>
        /// <param name="triggerName">Name of the trigger.</param>
        /// <param name="groupName">Name of the group.</param>
        /// <returns>a List of <see cref="FiredTriggerRecord" /> objects.</returns>
        public virtual async Task<IReadOnlyList<FiredTriggerRecord>> SelectFiredTriggerRecordsAsync(ConnectionAndTransactionHolder conn, string triggerName,
            string groupName)
        {
            DbCommand cmd;

            List<FiredTriggerRecord> lst = new List<FiredTriggerRecord>();

            if (triggerName != null)
            {
                cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectFiredTrigger));
                AddCommandParameter(cmd, "triggerName", triggerName);
                AddCommandParameter(cmd, "triggerGroup", groupName);
            }
            else
            {
                cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectFiredTriggerGroup));
                AddCommandParameter(cmd, "triggerGroup", groupName);
            }

            using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
            {
                while (await rs.ReadAsync().ConfigureAwait(false))
                {
                    FiredTriggerRecord rec = new FiredTriggerRecord();

                    rec.FireInstanceId = rs.GetString(ColumnEntryId);
                    rec.FireInstanceState = rs.GetString(ColumnEntryState);
                    rec.FireTimestamp = GetDateTimeFromDbValue(rs[ColumnFiredTime]) ?? DateTimeOffset.MinValue;
                    rec.ScheduleTimestamp = GetDateTimeFromDbValue(rs[ColumnScheduledTime]) ?? DateTimeOffset.MinValue;
                    rec.Priority = Convert.ToInt32(rs[ColumnPriority], CultureInfo.InvariantCulture);
                    rec.SchedulerInstanceId = rs.GetString(ColumnInstanceName);
                    rec.TriggerKey = new TriggerKey(rs.GetString(ColumnTriggerName), rs.GetString(ColumnTriggerGroup));
                    if (!rec.FireInstanceState.Equals(StateAcquired))
                    {
                        rec.JobDisallowsConcurrentExecution = GetBooleanFromDbValue(rs[ColumnIsNonConcurrent]);
                        rec.JobRequestsRecovery = GetBooleanFromDbValue(rs[ColumnRequestsRecovery]);
                        rec.JobKey = new JobKey(rs.GetString(ColumnJobName), rs.GetString(ColumnJobGroup));
                    }
                    lst.Add(rec);
                }
            }
            return lst;
        }

        /// <summary>
        /// Select the states of all fired-trigger records for a given job, or job
        /// group if job name is <see langword="null" />.
        /// </summary>
        /// <param name="conn">The DB connection.</param>
        /// <param name="jobName">Name of the job.</param>
        /// <param name="groupName">Name of the group.</param>
        /// <returns>a List of <see cref="FiredTriggerRecord" /> objects.</returns>
        public virtual async Task<IReadOnlyList<FiredTriggerRecord>> SelectFiredTriggerRecordsByJobAsync(ConnectionAndTransactionHolder conn, string jobName,
            string groupName)
        {
            List<FiredTriggerRecord> lst = new List<FiredTriggerRecord>();

            DbCommand cmd;
            if (jobName != null)
            {
                cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectFiredTriggersOfJob));
                AddCommandParameter(cmd, "jobName", jobName);
                AddCommandParameter(cmd, "jobGroup", groupName);
            }
            else
            {
                cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectFiredTriggersOfJobGroup));
                AddCommandParameter(cmd, "jobGroup", groupName);
            }

            using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
            {
                while (await rs.ReadAsync().ConfigureAwait(false))
                {
                    FiredTriggerRecord rec = new FiredTriggerRecord();

                    rec.FireInstanceId = rs.GetString(ColumnEntryId);
                    rec.FireInstanceState = rs.GetString(ColumnEntryState);
                    rec.FireTimestamp = GetDateTimeFromDbValue(rs[ColumnFiredTime]) ?? DateTimeOffset.MinValue;
                    rec.ScheduleTimestamp = GetDateTimeFromDbValue(rs[ColumnScheduledTime]) ?? DateTimeOffset.MinValue;
                    rec.Priority = Convert.ToInt32(rs[ColumnPriority], CultureInfo.InvariantCulture);
                    rec.SchedulerInstanceId = rs.GetString(ColumnInstanceName);
                    rec.TriggerKey = new TriggerKey(rs.GetString(ColumnTriggerName), rs.GetString(ColumnTriggerGroup));
                    if (!rec.FireInstanceState.Equals(StateAcquired))
                    {
                        rec.JobDisallowsConcurrentExecution = GetBooleanFromDbValue(rs[ColumnIsNonConcurrent]);
                        rec.JobRequestsRecovery = GetBooleanFromDbValue(rs[ColumnRequestsRecovery]);
                        rec.JobKey = new JobKey(rs.GetString(ColumnJobName), rs.GetString(ColumnJobGroup));
                    }
                    lst.Add(rec);
                }
            }
            return lst;
        }

        /// <summary>
        /// Select the states of all fired-trigger records for a given scheduler
        /// instance.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="instanceName">Name of the instance.</param>
        /// <returns>A list of FiredTriggerRecord objects.</returns>
        public virtual async Task<IReadOnlyList<FiredTriggerRecord>> SelectInstancesFiredTriggerRecordsAsync(ConnectionAndTransactionHolder conn, string instanceName)
        {
            List<FiredTriggerRecord> lst = new List<FiredTriggerRecord>();

            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectInstancesFiredTriggers)))
            {
                AddCommandParameter(cmd, "instanceName", instanceName);
                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    while (await rs.ReadAsync().ConfigureAwait(false))
                    {
                        FiredTriggerRecord rec = new FiredTriggerRecord();

                        rec.FireInstanceId = rs.GetString(ColumnEntryId);
                        rec.FireInstanceState = rs.GetString(ColumnEntryState);
                        rec.FireTimestamp = GetDateTimeFromDbValue(rs[ColumnFiredTime]) ?? DateTimeOffset.MinValue;
                        rec.ScheduleTimestamp = GetDateTimeFromDbValue(rs[ColumnScheduledTime]) ?? DateTimeOffset.MinValue;
                        rec.SchedulerInstanceId = rs.GetString(ColumnInstanceName);
                        rec.TriggerKey = new TriggerKey(rs.GetString(ColumnTriggerName), rs.GetString(ColumnTriggerGroup));
                        if (!rec.FireInstanceState.Equals(StateAcquired))
                        {
                            rec.JobDisallowsConcurrentExecution = GetBooleanFromDbValue(rs[ColumnIsNonConcurrent]);
                            rec.JobRequestsRecovery = GetBooleanFromDbValue(rs[ColumnRequestsRecovery]);
                            rec.JobKey = new JobKey(rs.GetString(ColumnJobName), rs.GetString(ColumnJobGroup));
                        }
                        lst.Add(rec);
                    }
                }

                return lst;
            }
        }

        /// <summary>
        /// Select the distinct instance names of all fired-trigger records.
        /// </summary>
        /// <param name="conn">The conn.</param>
        /// <returns></returns>
        /// <remarks>
        /// This is useful when trying to identify orphaned fired triggers (a
        /// fired trigger without a scheduler state record.)
        /// </remarks>
        public virtual async Task<ISet<string>> SelectFiredTriggerInstanceNamesAsync(ConnectionAndTransactionHolder conn)
        {
            var instanceNames = new HashSet<string>();
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectFiredTriggerInstanceNames)))
            {
                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    while (await rs.ReadAsync().ConfigureAwait(false))
                    {
                        instanceNames.Add(rs.GetString(ColumnInstanceName));
                    }

                    return instanceNames;
                }
            }
        }

        /// <summary>
        /// Delete a fired trigger.
        /// </summary>
        /// <param name="conn">the DB Connection</param>
        /// <param name="entryId">the fired trigger entry to delete</param>
        /// <returns>the number of rows deleted</returns>
        public virtual async Task<int> DeleteFiredTriggerAsync(ConnectionAndTransactionHolder conn, string entryId)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlDeleteFiredTrigger)))
            {
                AddCommandParameter(cmd, "triggerEntryId", entryId);
                return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Selects the job execution count.
        /// </summary>
        /// <param name="conn">The DB connection.</param>
        /// <param name="jobKey">The key of the job.</param>
        /// <returns></returns>
        public virtual async Task<int> SelectJobExecutionCountAsync(ConnectionAndTransactionHolder conn, JobKey jobKey)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectJobExecutionCount)))
            {
                AddCommandParameter(cmd, "jobName", jobKey.Name);
                AddCommandParameter(cmd, "jobGroup", jobKey.Group);

                using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    if (await rs.ReadAsync().ConfigureAwait(false))
                    {
                        return Convert.ToInt32(rs.GetValue(0), CultureInfo.InvariantCulture);
                    }

                    return 0;
                }
            }
        }

        /// <summary>
        /// Inserts the state of the scheduler.
        /// </summary>
        /// <param name="conn">The conn.</param>
        /// <param name="instanceName">The instance id.</param>
        /// <param name="checkInTime">The check in time.</param>
        /// <param name="interval">The interval.</param>
        /// <returns></returns>
        public virtual async Task<int> InsertSchedulerStateAsync(ConnectionAndTransactionHolder conn, string instanceName, DateTimeOffset checkInTime, TimeSpan interval)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlInsertSchedulerState)))
            {
                AddCommandParameter(cmd, "instanceName", instanceName);
                AddCommandParameter(cmd, "lastCheckinTime", GetDbDateTimeValue(checkInTime));
                AddCommandParameter(cmd, "checkinInterval", GetDbTimeSpanValue(interval));

                return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Deletes the state of the scheduler.
        /// </summary>
        /// <param name="conn">The database connection.</param>
        /// <param name="instanceName">The instance id.</param>
        /// <returns></returns>
        public virtual async Task<int> DeleteSchedulerStateAsync(ConnectionAndTransactionHolder conn, string instanceName)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlDeleteSchedulerState)))
            {
                AddCommandParameter(cmd, "instanceName", instanceName);

                return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Updates the state of the scheduler.
        /// </summary>
        /// <param name="conn">The database connection.</param>
        /// <param name="instanceName">The instance id.</param>
        /// <param name="checkInTime">The check in time.</param>
        /// <returns></returns>
        public virtual async Task<int> UpdateSchedulerStateAsync(ConnectionAndTransactionHolder conn, string instanceName, DateTimeOffset checkInTime)
        {
            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlUpdateSchedulerState)))
            {
                AddCommandParameter(cmd, "lastCheckinTime", GetDbDateTimeValue(checkInTime));
                AddCommandParameter(cmd, "instanceName", instanceName);

                return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// A List of all current <see cref="SchedulerStateRecord" />s.
        /// <para>
        /// If instanceId is not null, then only the record for the identified
        /// instance will be returned.
        /// </para>
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="instanceName">The instance id.</param>
        /// <returns></returns>
        public virtual async Task<IReadOnlyList<SchedulerStateRecord>> SelectSchedulerStateRecordsAsync(ConnectionAndTransactionHolder conn, string instanceName)
        {
            DbCommand cmd;
            List<SchedulerStateRecord> list = new List<SchedulerStateRecord>();

            if (instanceName != null)
            {
                cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectSchedulerState));
                AddCommandParameter(cmd, "instanceName", instanceName);
            }
            else
            {
                cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectSchedulerStates));
            }
            using (var rs = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
            {
                while (await rs.ReadAsync().ConfigureAwait(false))
                {
                    SchedulerStateRecord rec = new SchedulerStateRecord();
                    rec.SchedulerInstanceId = rs.GetString(ColumnInstanceName);
                    rec.CheckinTimestamp = GetDateTimeFromDbValue(rs[ColumnLastCheckinTime]) ?? DateTimeOffset.MinValue;
                    rec.CheckinInterval = GetTimeSpanFromDbValue(rs[ColumnCheckinInterval]) ?? TimeSpan.Zero;
                    list.Add(rec);
                }
            }
            return list;
        }

        //---------------------------------------------------------------------------
        // protected methods that can be overridden by subclasses
        //---------------------------------------------------------------------------

        /// <summary>
        /// Replace the table prefix in a query by replacing any occurrences of
        /// "{0}" with the table prefix.
        /// </summary>
        /// <param name="query">The unsubstituted query</param>
        /// <returns>The query, with proper table prefix substituted</returns>
        protected string ReplaceTablePrefix(string query)
        {
            return AdoJobStoreUtil.ReplaceTablePrefix(query, tablePrefix, SchedulerNameLiteral);
        }

        protected string SchedulerNameLiteral
        {
            get
            {
                if (schedNameLiteral == null)
                {
                    schedNameLiteral = "'" + schedName + "'";
                }
                return schedNameLiteral;
            }
        }

        /// <summary>
        /// Create a serialized <see langword="byte[]"/> version of an Object.
        /// </summary>
        /// <param name="obj">the object to serialize</param>
        /// <returns>Serialized object as byte array.</returns>
        protected virtual byte[] SerializeObject(object obj)
        {
            byte[] retValue = null;
            if (obj != null)
            {
                retValue = objectSerializer.Serialize(obj);
            }
            return retValue;
        }

        /// <summary>
        /// Remove the transient data from and then create a serialized <see cref="MemoryStream" />
        /// version of a <see cref="JobDataMap" /> and returns the underlying bytes.
        /// </summary>
        /// <param name="data">The data.</param>
        /// <returns>the serialized data as byte array</returns>
        public virtual byte[] SerializeJobData(JobDataMap data)
        {
            if (CanUseProperties)
            {
                return SerializeProperties(data);
            }

            try
            {
                return SerializeObject(data);
            }
            catch (SerializationException e)
            {
                throw new SerializationException(
                    "Unable to serialize JobDataMap for insertion into " +
                    "database because the value of property '" +
                    GetKeyOfNonSerializableValue(data) +
                    "' is not serializable: " + e.Message);
            }
        }

        protected object GetKeyOfNonSerializableValue(IDictionary data)
        {
            foreach (DictionaryEntry entry in data)
            {
                try
                {
                    SerializeObject(entry.Value);
                }
                catch (Exception)
                {
                    return entry.Key;
                }
            }

            // As long as it is true that the Map was not serializable, we should
            // not hit this case.
            return null;
        }

        /// <summary>
        /// serialize
        /// </summary>
        /// <param name="data">The data.</param>
        /// <returns></returns>
        private byte[] SerializeProperties(JobDataMap data)
        {
            byte[] retValue = null;
            if (null != data)
            {
                NameValueCollection properties = ConvertToProperty(data.WrappedMap);
                retValue = SerializeObject(properties);
            }

            return retValue;
        }

        /// <summary> 
        /// Convert the JobDataMap into a list of properties.
        /// </summary>
        protected virtual IDictionary ConvertFromProperty(NameValueCollection properties)
        {
            IDictionary<string, string> data = new Dictionary<string, string>();
            foreach (string key in properties.AllKeys)
            {
                data[key] = properties[key];
            }

            return (IDictionary) data;
        }

        /// <summary>
        /// Convert the JobDataMap into a list of properties.
        /// </summary>
        protected virtual NameValueCollection ConvertToProperty(IDictionary<string, object> data)
        {
            NameValueCollection properties = new NameValueCollection();
            foreach (KeyValuePair<string, object> entry in data)
            {
                object key = entry.Key;
                object val = entry.Value ?? string.Empty;

                if (!(key is string))
                {
                    throw new IOException("JobDataMap keys/values must be Strings " +
                                          "when the 'useProperties' property is set. " +
                                          " offending Key: " + key);
                }
                if (!(val is string))
                {
                    throw new IOException("JobDataMap values must be Strings " +
                                          "when the 'useProperties' property is set. " +
                                          " Key of offending value: " + key);
                }
                properties[(string) key] = (string) val;
            }
            return properties;
        }

        /// <summary>
        /// This method should be overridden by any delegate subclasses that need
        /// special handling for BLOBs. The default implementation uses standard
        /// ADO.NET operations.
        /// </summary>
        /// <param name="rs">The data reader, already queued to the correct row.</param>
        /// <param name="colIndex">The column index for the BLOB.</param>
        /// <returns>The deserialized object from the DataReader BLOB.</returns>
        protected virtual async Task<T> GetObjectFromBlob<T>(DbDataReader rs, int colIndex) where T : class
        {
            T obj = null;

            byte[] data = await ReadBytesFromBlob(rs, colIndex).ConfigureAwait(false);
            if (data != null && data.Length > 0)
            {
                obj = objectSerializer.DeSerialize<T>(data);
            }
            return obj;
        }

        protected virtual async Task<byte[]> ReadBytesFromBlob(IDataReader dr, int colIndex)
        {
            if (dr.IsDBNull(colIndex))
            {
                return null;
            }

            byte[] outbyte = new byte[dr.GetBytes(colIndex, 0, null, 0, int.MaxValue)];
            dr.GetBytes(colIndex, 0, outbyte, 0, outbyte.Length);
            using (MemoryStream stream = new MemoryStream())
            {
                await stream.WriteAsync(outbyte, 0, outbyte.Length).ConfigureAwait(false);
            }
            return outbyte;
        }

        /// <summary>
        /// This method should be overridden by any delegate subclasses that need
        /// special handling for BLOBs for job details. 
        /// </summary>
        /// <param name="rs">The result set, already queued to the correct row.</param>
        /// <param name="colIndex">The column index for the BLOB.</param>
        /// <returns>The deserialized Object from the ResultSet BLOB.</returns>
        protected virtual Task<T> GetJobDataFromBlob<T>(DbDataReader rs, int colIndex) where T : class
        {
            if (CanUseProperties)
            {
                if (!rs.IsDBNull(colIndex))
                {
                    // should be NameValueCollection
                    return GetObjectFromBlob<T>(rs, colIndex);
                }

                return null;
            }

            return GetObjectFromBlob<T>(rs, colIndex);
        }

        /// <summary>
        /// Selects the paused trigger groups.
        /// </summary>
        /// <param name="conn">The DB Connection.</param>
        /// <returns></returns>
        public virtual async Task<ISet<string>> SelectPausedTriggerGroupsAsync(ConnectionAndTransactionHolder conn)
        {
            HashSet<string> retValue = new HashSet<string>();

            using (var cmd = PrepareCommand(conn, ReplaceTablePrefix(SqlSelectPausedTriggerGroups)))
            {
                using (var dr = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    while (await dr.ReadAsync().ConfigureAwait(false))
                    {
                        string groupName = (string) dr[ColumnTriggerGroup];
                        retValue.Add(groupName);
                    }
                }
                return retValue;
            }
        }

        public virtual DbCommand PrepareCommand(ConnectionAndTransactionHolder cth, string commandText)
        {
            return adoUtil.PrepareCommand(cth, commandText);
        }

        public virtual void AddCommandParameter(DbCommand cmd, string paramName, object paramValue)
        {
            AddCommandParameter(cmd, paramName, paramValue, null);
        }

        public virtual void AddCommandParameter(DbCommand cmd, string paramName, object paramValue, Enum dataType)
        {
            adoUtil.AddCommandParameter(cmd, paramName, paramValue, dataType);
        }
    }
}