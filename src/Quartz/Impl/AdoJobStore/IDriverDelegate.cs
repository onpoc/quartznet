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
using System.Collections.Generic;
using System.Threading.Tasks;

using Quartz.Impl.Matchers;
using Quartz.Spi;

namespace Quartz.Impl.AdoJobStore
{
    /// <summary>
    /// This is the base interface for all driver delegate classes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This interface is very similar to the <see cref="IJobStore" />
    /// interface except each method has an additional <see cref="ConnectionAndTransactionHolder" />
    /// parameter.
    /// </para>
    /// <para>
    /// Unless a database driver has some <strong>extremely-DB-specific</strong>
    /// requirements, any IDriverDelegate implementation classes should extend the
    /// <see cref="StdAdoDelegate" /> class.
    /// </para>
    /// </remarks>
    /// <author><a href="mailto:jeff@binaryfeed.org">Jeffrey Wescott</a></author>
    /// <author>James House</author>
    /// <author>Marko Lahma (.NET)</author>
    public interface IDriverDelegate
    {
        /// <summary>
        /// Initializes the driver delegate with configuration data.
        /// </summary>
        /// <param name="args"></param>
        void Initialize(DelegateInitializationArgs args);

        /// <summary>
        /// Update all triggers having one of the two given states, to the given new
        /// state.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="newState">The new state for the triggers</param>
        /// <param name="oldState1">The first old state to update</param>
        /// <param name="oldState2">The second old state to update</param>
        /// <returns>Number of rows updated</returns>
        Task<int> UpdateTriggerStatesFromOtherStatesAsync(ConnectionAndTransactionHolder conn, string newState, string oldState1, string oldState2);

        /// <summary>
        /// Get the names of all of the triggers that have misfired - according to
        /// the given timestamp.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="timestamp">The timestamp.</param>
        /// <returns>An array of <see cref="TriggerKey" /> objects</returns>
        Task<IReadOnlyList<TriggerKey>> SelectMisfiredTriggersAsync(ConnectionAndTransactionHolder conn, DateTimeOffset timestamp);

        /// <summary>
        /// Get the names of all of the triggers in the given state that have
        /// misfired - according to the given timestamp.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="state">The state.</param>
        /// <param name="ts">The time stamp.</param>
        /// <returns>An array of <see cref="TriggerKey" /> objects</returns>
        Task<IReadOnlyList<TriggerKey>> HasMisfiredTriggersInStateAsync(ConnectionAndTransactionHolder conn, string state, DateTimeOffset ts);

        /// <summary>
        /// Get the names of all of the triggers in the given group and state that
        /// have misfired - according to the given timestamp.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="groupName">Name of the group.</param>
        /// <param name="state">The state.</param>
        /// <param name="ts">The timestamp.</param>
        /// <returns>An array of <see cref="TriggerKey" /> objects</returns>
        Task<IReadOnlyList<TriggerKey>> SelectMisfiredTriggersInGroupInStateAsync(ConnectionAndTransactionHolder conn, string groupName, string state, DateTimeOffset ts);

        /// <summary> 
        /// Select all of the triggers for jobs that are requesting recovery. The
        /// returned trigger objects will have unique "recoverXXX" trigger names and
        /// will be in the <see cref="SchedulerConstants.DefaultRecoveryGroup" /> trigger group.
        /// </summary>
        /// <remarks>
        /// In order to preserve the ordering of the triggers, the fire time will be
        /// set from the <i>ColumnFiredTime</i> column in the <i>TableFiredTriggers</i>
        /// table. The caller is responsible for calling <see cref="IOperableTrigger.ComputeFirstFireTimeUtc" />
        /// on each returned trigger. It is also up to the caller to insert the
        /// returned triggers to ensure that they are fired.
        /// </remarks>
        /// <param name="conn">The DB Connection</param>
        /// <returns>An array of <see cref="ITrigger" /> objects</returns>
        Task<IReadOnlyList<IOperableTrigger>> SelectTriggersForRecoveringJobsAsync(ConnectionAndTransactionHolder conn);

        /// <summary>
        /// Delete all fired triggers.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <returns>The number of rows deleted</returns>
        Task<int> DeleteFiredTriggersAsync(ConnectionAndTransactionHolder conn);

        /// <summary>
        /// Delete all fired triggers of the given instance.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="instanceId">The instance id.</param>
        /// <returns>The number of rows deleted</returns>
        Task<int> DeleteFiredTriggersAsync(ConnectionAndTransactionHolder conn, string instanceId);

        //---------------------------------------------------------------------------
        // jobs
        //---------------------------------------------------------------------------

        /// <summary>
        /// Insert the job detail record.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="job">The job to insert.</param>
        /// <returns>Number of rows inserted.</returns>
        Task<int> InsertJobDetailAsync(ConnectionAndTransactionHolder conn, IJobDetail job);

        /// <summary>
        /// Update the job detail record.
        /// </summary>
        /// <param name="conn">The DB Connection.</param>
        /// <param name="job">The job to update.</param>
        /// <returns>Number of rows updated.</returns>
        Task<int> UpdateJobDetailAsync(ConnectionAndTransactionHolder conn, IJobDetail job);

        /// <summary> <para>
        /// Get the names of all of the triggers associated with the given job.
        /// </para>
        /// 
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="jobKey">The key identifying the job.</param>
        Task<IReadOnlyList<TriggerKey>> SelectTriggerNamesForJobAsync(ConnectionAndTransactionHolder conn, JobKey jobKey);

        /// <summary>
        /// Delete the job detail record for the given job.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="jobKey">The key identifying the job.</param>
        /// <returns>the number of rows deleted</returns>
        Task<int> DeleteJobDetailAsync(ConnectionAndTransactionHolder conn, JobKey jobKey);

        /// <summary>
        /// Check whether or not the given job is stateful.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="jobKey">The key identifying the job.</param>
        /// <returns> true if the job exists and is stateful, false otherwise</returns>
        Task<bool> IsJobStatefulAsync(ConnectionAndTransactionHolder conn, JobKey jobKey);

        /// <summary>
        /// Check whether or not the given job exists.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="jobKey">The key identifying the job.</param>
        /// <returns>true if the job exists, false otherwise</returns>
        Task<bool> JobExistsAsync(ConnectionAndTransactionHolder conn, JobKey jobKey);

        /// <summary>
        /// Update the job data map for the given job.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="job">The job.</param>
        /// <returns>the number of rows updated</returns>
        Task<int> UpdateJobDataAsync(ConnectionAndTransactionHolder conn, IJobDetail job);

        /// <summary>
        /// Select the JobDetail object for a given job name / group name.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="jobKey">The key identifying the job.</param>
        /// <param name="classLoadHelper">The class load helper.</param>
        /// <returns>The populated JobDetail object</returns>
        Task<IJobDetail> SelectJobDetailAsync(ConnectionAndTransactionHolder conn, JobKey jobKey, ITypeLoadHelper classLoadHelper);

        /// <summary>
        /// Select the total number of jobs stored.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <returns> the total number of jobs stored</returns>
        Task<int> SelectNumJobsAsync(ConnectionAndTransactionHolder conn);

        /// <summary> 
        /// Select all of the job group names that are stored.
        /// </summary>
        /// <param name="conn">The DB Connection.</param>
        /// <returns> an array of <see cref="String" /> group names</returns>
        Task<IReadOnlyList<string>> SelectJobGroupsAsync(ConnectionAndTransactionHolder conn);

        /// <summary>
        /// Select all of the jobs contained in a given group.
        /// </summary>
        /// <param name="conn">The DB Connection </param>
        /// <param name="matcher"></param>
        /// <returns> an array of <see cref="String" /> job names</returns>
        Task<ISet<JobKey>> SelectJobsInGroupAsync(ConnectionAndTransactionHolder conn, GroupMatcher<JobKey> matcher);

        //---------------------------------------------------------------------------
        // triggers
        //---------------------------------------------------------------------------

        /// <summary>
        /// Insert the base trigger data.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="trigger">The trigger to insert.</param>
        /// <param name="state">The state that the trigger should be stored in.</param>
        /// <param name="jobDetail">The job detail.</param>
        /// <returns>The number of rows inserted</returns>
        Task<int> InsertTriggerAsync(ConnectionAndTransactionHolder conn, IOperableTrigger trigger, string state, IJobDetail jobDetail);

        /// <summary>
        /// Insert the blob trigger data.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="trigger">The trigger to insert</param>
        /// <returns>The number of rows inserted</returns>
        Task<int> InsertBlobTriggerAsync(ConnectionAndTransactionHolder conn, IOperableTrigger trigger);

        /// <summary>
        /// Update the base trigger data.
        /// </summary>
        /// <param name="conn">the DB Connection</param>
        /// <param name="trigger">The trigger.</param>
        /// <param name="state">The state.</param>
        /// <param name="jobDetail">The job detail.</param>
        /// <returns>the number of rows updated</returns>
        Task<int> UpdateTriggerAsync(ConnectionAndTransactionHolder conn, IOperableTrigger trigger, string state, IJobDetail jobDetail);

        /// <summary>
        /// Update the blob trigger data.
        /// </summary>
        /// <param name="conn">the DB Connection</param>
        /// <param name="trigger">The trigger.</param>
        /// <returns>the number of rows updated</returns>
        Task<int> UpdateBlobTriggerAsync(ConnectionAndTransactionHolder conn, IOperableTrigger trigger);

        /// <summary>
        /// Check whether or not a trigger exists.
        /// </summary>
        /// <param name="conn">the DB Connection</param>
        /// <param name="triggerKey">The key identifying the trigger.</param>
        /// <returns>the number of rows updated</returns>
        Task<bool> TriggerExistsAsync(ConnectionAndTransactionHolder conn, TriggerKey triggerKey);

        /// <summary>
        /// Update the state for a given trigger.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="triggerKey">The key identifying the trigger.</param>
        /// <param name="state">The new state for the trigger.</param>
        /// <returns> the number of rows updated</returns>
        Task<int> UpdateTriggerStateAsync(ConnectionAndTransactionHolder conn, TriggerKey triggerKey, string state);

        /// <summary>
        /// Update the given trigger to the given new state, if it is in the given
        /// old state.
        /// </summary>
        /// <param name="conn">The DB connection</param>
        /// <param name="triggerKey">The key identifying the trigger.</param>
        /// <param name="newState">The new state for the trigger </param>
        /// <param name="oldState">The old state the trigger must be in</param>
        /// <returns> int the number of rows updated</returns>
        Task<int> UpdateTriggerStateFromOtherStateAsync(ConnectionAndTransactionHolder conn, TriggerKey triggerKey, string newState,
            string oldState);

        /// <summary>
        /// Update the given trigger to the given new state, if it is one of the
        /// given old states.
        /// </summary>
        /// <param name="conn">The DB connection</param>
        /// <param name="triggerKey">The key identifying the trigger.</param>
        /// <param name="newState">The new state for the trigger</param>
        /// <param name="oldState1">One of the old state the trigger must be in</param>
        /// <param name="oldState2">One of the old state the trigger must be in</param>
        /// <param name="oldState3">One of the old state the trigger must be in
        /// </param>
        /// <returns> int the number of rows updated
        /// </returns>
        /// <throws>  SQLException </throws>
        Task<int> UpdateTriggerStateFromOtherStatesAsync(ConnectionAndTransactionHolder conn, TriggerKey triggerKey, string newState,
            string oldState1, string oldState2, string oldState3);

        /// <summary>
        /// Update all triggers in the given group to the given new state, if they
        /// are in one of the given old states.
        /// </summary>
        /// <param name="conn">The DB connection</param>
        /// <param name="matcher"></param>
        /// <param name="newState">The new state for the trigger</param>
        /// <param name="oldState1">One of the old state the trigger must be in</param>
        /// <param name="oldState2">One of the old state the trigger must be in</param>
        /// <param name="oldState3">One of the old state the trigger must be in</param>
        /// <returns>The number of rows updated</returns>
        Task<int> UpdateTriggerGroupStateFromOtherStatesAsync(ConnectionAndTransactionHolder conn, GroupMatcher<TriggerKey> matcher, string newState, string oldState1, string oldState2, string oldState3);

        /// <summary>
        /// Update all of the triggers of the given group to the given new state, if
        /// they are in the given old state.
        /// </summary>
        /// <param name="conn">The DB connection</param>
        /// <param name="matcher"></param>
        /// <param name="newState">The new state for the trigger group</param>
        /// <param name="oldState">The old state the triggers must be in.</param>
        /// <returns> int the number of rows updated</returns>
        Task<int> UpdateTriggerGroupStateFromOtherStateAsync(ConnectionAndTransactionHolder conn, GroupMatcher<TriggerKey> matcher, string newState, string oldState);

        /// <summary>
        /// Update the states of all triggers associated with the given job.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="jobKey">The key identifying the job.</param>
        /// <param name="state">The new state for the triggers.</param>
        /// <returns>The number of rows updated</returns>
        Task<int> UpdateTriggerStatesForJobAsync(ConnectionAndTransactionHolder conn, JobKey jobKey, string state);

        /// <summary>
        /// Update the states of any triggers associated with the given job, that
        /// are the given current state.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="jobKey">The key identifying the job.</param>
        /// <param name="state">The new state for the triggers</param>
        /// <param name="oldState">The old state of the triggers</param>
        /// <returns> the number of rows updated</returns>
        Task<int> UpdateTriggerStatesForJobFromOtherStateAsync(ConnectionAndTransactionHolder conn, JobKey jobKey, string state, string oldState);

        /// <summary>
        /// Delete the BLOB trigger data for a trigger.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="triggerKey">The key identifying the trigger.</param>
        /// <returns>The number of rows deleted</returns>
        Task<int> DeleteBlobTriggerAsync(ConnectionAndTransactionHolder conn, TriggerKey triggerKey);

        /// <summary>
        /// Delete the base trigger data for a trigger.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="triggerKey">The key identifying the trigger.</param>
        /// <returns> the number of rows deleted </returns>
        Task<int> DeleteTriggerAsync(ConnectionAndTransactionHolder conn, TriggerKey triggerKey);

        /// <summary>
        /// Select the number of triggers associated with a given job.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="jobKey">The key identifying the job.</param>
        /// <returns> the number of triggers for the given job </returns>
        Task<int> SelectNumTriggersForJobAsync(ConnectionAndTransactionHolder conn, JobKey jobKey);

        /// <summary>
        /// Select the job to which the trigger is associated.
        /// </summary>
        Task<IJobDetail> SelectJobForTriggerAsync(ConnectionAndTransactionHolder conn, TriggerKey triggerKey, ITypeLoadHelper loadHelper);

        /// <summary>
        /// Select the job to which the trigger is associated. Allow option to load actual job class or not. When case of
        /// remove, we do not need to load the type, which in many cases, it's no longer exists.
        /// </summary>
        Task<IJobDetail> SelectJobForTriggerAsync(ConnectionAndTransactionHolder conn, TriggerKey triggerKey, ITypeLoadHelper loadHelper, bool loadJobType);

        /// <summary>
        /// Select the triggers for a job>
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="jobKey">The key identifying the job.</param>
        /// <returns> an array of <see cref="ITrigger" /> objects associated with a given job. </returns>
        Task<IReadOnlyList<IOperableTrigger>> SelectTriggersForJobAsync(ConnectionAndTransactionHolder conn, JobKey jobKey);

        /// <summary>
        /// Select the triggers for a calendar
        /// </summary>
        /// <param name="conn">The DB Connection.</param>
        /// <param name="calName">Name of the calendar.</param>
        /// <returns>
        /// An array of <see cref="ITrigger" /> objects associated with a given job.
        /// </returns>
        Task<IReadOnlyList<IOperableTrigger>> SelectTriggersForCalendarAsync(ConnectionAndTransactionHolder conn, string calName);

        /// <summary>
        /// Select a trigger.
        /// </summary>
        /// <param name="conn">The DB Connection.</param>
        /// <param name="triggerKey">The key identifying the trigger.</param>
        /// <returns>The <see cref="ITrigger" /> object.
        /// </returns>
        Task<IOperableTrigger> SelectTriggerAsync(ConnectionAndTransactionHolder conn, TriggerKey triggerKey);

        /// <summary>
        /// Select a trigger's JobDataMap.
        /// </summary>
        /// <param name="conn">The DB Connection.</param>
        /// <param name="triggerKey">The key identifying the trigger.</param>
        /// <returns>The <see cref="JobDataMap" /> of the Trigger, never null, but possibly empty.</returns>
        Task<JobDataMap> SelectTriggerJobDataMapAsync(ConnectionAndTransactionHolder conn, TriggerKey triggerKey);

        /// <summary>
        /// Select a trigger's state value.
        /// </summary>
        /// <param name="conn">The DB Connection.</param>
        /// <param name="triggerKey">The key identifying the trigger.</param>
        /// <returns>The <see cref="ITrigger" /> object.</returns>
        Task<string> SelectTriggerStateAsync(ConnectionAndTransactionHolder conn, TriggerKey triggerKey);

        /// <summary> 
        /// Select a triggers status (state and next fire time).
        /// </summary>
        /// <param name="conn">The DB Connection.</param>
        /// <param name="triggerKey">The key identifying the trigger.</param>
        /// <returns>A <see cref="TriggerStatus" /> object, or null</returns>
        Task<TriggerStatus> SelectTriggerStatusAsync(ConnectionAndTransactionHolder conn, TriggerKey triggerKey);

        /// <summary>
        /// Select the total number of triggers stored.
        /// </summary>
        /// <param name="conn">The DB Connection.</param>
        /// <returns>The total number of triggers stored.</returns>
        Task<int> SelectNumTriggersAsync(ConnectionAndTransactionHolder conn);

        /// <summary>
        /// Select all of the trigger group names that are stored.
        /// </summary>
        /// <param name="conn">The DB Connection.</param>
        /// <returns>An array of <see cref="String" /> group names.</returns>
        Task<IReadOnlyList<string>> SelectTriggerGroupsAsync(ConnectionAndTransactionHolder conn);

        Task<IReadOnlyList<string>> SelectTriggerGroupsAsync(ConnectionAndTransactionHolder conn, GroupMatcher<TriggerKey> matcher);

        /// <summary>
        /// Select all of the triggers contained in a given group. 
        /// </summary>
        /// <param name="conn">The DB Connection.</param>
        /// <param name="matcher"></param>
        /// <returns>An array of <see cref="String" /> trigger names.</returns>
        Task<ISet<TriggerKey>> SelectTriggersInGroupAsync(ConnectionAndTransactionHolder conn, GroupMatcher<TriggerKey> matcher);

        /// <summary>
        /// Select all of the triggers in a given state.
        /// </summary>
        /// <param name="conn">The DB Connection.</param>
        /// <param name="state">The state the triggers must be in.</param>
        /// <returns>An array of trigger <see cref="TriggerKey" />s.</returns>
        Task<IReadOnlyList<TriggerKey>> SelectTriggersInStateAsync(ConnectionAndTransactionHolder conn, string state);

        /// <summary>
        /// Inserts the paused trigger group.
        /// </summary>
        /// <param name="conn">The conn.</param>
        /// <param name="groupName">Name of the group.</param>
        /// <returns></returns>
        Task<int> InsertPausedTriggerGroupAsync(ConnectionAndTransactionHolder conn, string groupName);

        /// <summary>
        /// Deletes the paused trigger group.
        /// </summary>
        /// <param name="conn">The conn.</param>
        /// <param name="groupName">Name of the group.</param>
        /// <returns></returns>
        Task<int> DeletePausedTriggerGroupAsync(ConnectionAndTransactionHolder conn, string groupName);

        Task<int> DeletePausedTriggerGroupAsync(ConnectionAndTransactionHolder conn, GroupMatcher<TriggerKey> matcher);

        /// <summary>
        /// Deletes all paused trigger groups.
        /// </summary>
        /// <param name="conn">The conn.</param>
        /// <returns></returns>
        Task<int> DeleteAllPausedTriggerGroupsAsync(ConnectionAndTransactionHolder conn);

        /// <summary>
        /// Determines whether the specified trigger group is paused.
        /// </summary>
        /// <param name="conn">The conn.</param>
        /// <param name="groupName">Name of the group.</param>
        /// <returns>
        /// 	<c>true</c> if trigger group is paused; otherwise, <c>false</c>.
        /// </returns>
        Task<bool> IsTriggerGroupPausedAsync(ConnectionAndTransactionHolder conn, string groupName);

        /// <summary>
        /// Selects the paused trigger groups.
        /// </summary>
        /// <param name="conn">The DB Connection.</param>
        /// <returns></returns>
        Task<ISet<string>> SelectPausedTriggerGroupsAsync(ConnectionAndTransactionHolder conn);

        /// <summary>
        /// Determines whether given trigger group already exists.
        /// </summary>
        /// <param name="conn">The conn.</param>
        /// <param name="groupName">Name of the group.</param>
        /// <returns>
        /// 	<c>true</c> if trigger group exists; otherwise, <c>false</c>.
        /// </returns>
        Task<bool> IsExistingTriggerGroupAsync(ConnectionAndTransactionHolder conn, string groupName);

        //---------------------------------------------------------------------------
        // calendars
        //---------------------------------------------------------------------------

        /// <summary>
        /// Insert a new calendar.
        /// </summary>
        /// <param name="conn">The DB Connection.</param>
        /// <param name="calendarName">The name for the new calendar.</param>
        /// <param name="calendar">The calendar.</param>
        /// <returns>The number of rows inserted.</returns>
        Task<int> InsertCalendarAsync(ConnectionAndTransactionHolder conn, string calendarName, ICalendar calendar);

        /// <summary> 
        /// Update a calendar.
        /// </summary>
        /// <param name="conn">The DB Connection.</param>
        /// <param name="calendarName">The name for the new calendar.</param>
        /// <param name="calendar">The calendar.</param>
        /// <returns>The number of rows updated.</returns>
        Task<int> UpdateCalendarAsync(ConnectionAndTransactionHolder conn, string calendarName, ICalendar calendar);

        /// <summary>
        /// Check whether or not a calendar exists.
        /// </summary>
        /// <param name="conn">The DB Connection.</param>
        /// <param name="calendarName">The name of the calendar.</param>
        /// <returns>true if the trigger exists, false otherwise.</returns>
        Task<bool> CalendarExistsAsync(ConnectionAndTransactionHolder conn, string calendarName);

        /// <summary>
        /// Select a calendar.
        /// </summary>
        /// <param name="conn">The DB Connection.</param>
        /// <param name="calendarName">The name of the calendar.</param>
        /// <returns>The Calendar.</returns>
        Task<ICalendar> SelectCalendarAsync(ConnectionAndTransactionHolder conn, string calendarName);

        /// <summary>
        /// Check whether or not a calendar is referenced by any triggers.
        /// </summary>
        /// <param name="conn">The DB Connection.</param>
        /// <param name="calendarName">The name of the calendar.</param>
        /// <returns>true if any triggers reference the calendar, false otherwise</returns>
        Task<bool> CalendarIsReferencedAsync(ConnectionAndTransactionHolder conn, string calendarName);

        /// <summary>
        /// Delete a calendar.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="calendarName">The name of the trigger.</param>
        /// <returns>The number of rows deleted.</returns>
        Task<int> DeleteCalendarAsync(ConnectionAndTransactionHolder conn, string calendarName);

        /// <summary> 
        /// Select the total number of calendars stored.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <returns>The total number of calendars stored.</returns>
        Task<int> SelectNumCalendarsAsync(ConnectionAndTransactionHolder conn);

        /// <summary>
        /// Select all of the stored calendars.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <returns>An array of <see cref="String" /> calendar names.</returns>
        Task<IReadOnlyList<string>> SelectCalendarsAsync(ConnectionAndTransactionHolder conn);

        //---------------------------------------------------------------------------
        // trigger firing
        //---------------------------------------------------------------------------

        /// <summary>
        /// Select the trigger that will be fired at the given fire time.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="fireTime">The time that the trigger will be fired.</param>
        /// <returns> 
        /// A <see cref="TriggerKey" /> representing the
        /// trigger that will be fired at the given fire time, or null if no
        /// trigger will be fired at that time
        /// </returns>
        Task<TriggerKey> SelectTriggerForFireTimeAsync(ConnectionAndTransactionHolder conn, DateTimeOffset fireTime);

        /// <summary>
        /// Insert a fired trigger.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="trigger">The trigger.</param>
        /// <param name="state">The state that the trigger should be stored in.</param>
        /// <param name="jobDetail">The job detail.</param>
        /// <returns>The number of rows inserted.</returns>
        Task<int> InsertFiredTriggerAsync(ConnectionAndTransactionHolder conn, IOperableTrigger trigger, string state, IJobDetail jobDetail);

        /// <summary>
        /// Select the states of all fired-trigger records for a given trigger, or
        /// trigger group if trigger name is <see langword="null" />.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="triggerName">Name of the trigger.</param>
        /// <param name="groupName">Name of the group.</param>
        /// <returns>A list of FiredTriggerRecord objects.</returns>
        Task<IReadOnlyList<FiredTriggerRecord>> SelectFiredTriggerRecordsAsync(ConnectionAndTransactionHolder conn, string triggerName, string groupName);

        /// <summary>
        /// Select the states of all fired-trigger records for a given job, or job
        /// group if job name is <see langword="null" />.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="jobName">Name of the job.</param>
        /// <param name="groupName">Name of the group.</param>
        /// <returns>A List of FiredTriggerRecord objects.</returns>
        Task<IReadOnlyList<FiredTriggerRecord>> SelectFiredTriggerRecordsByJobAsync(ConnectionAndTransactionHolder conn, string jobName, string groupName);

        /// <summary>
        /// Select the states of all fired-trigger records for a given scheduler
        /// instance.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="instanceName">Name of the instance.</param>
        /// <returns>A list of FiredTriggerRecord objects.</returns>
        Task<IReadOnlyList<FiredTriggerRecord>> SelectInstancesFiredTriggerRecordsAsync(ConnectionAndTransactionHolder conn, string instanceName);

        /// <summary>
        /// Delete a fired trigger.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="entryId">The fired trigger entry to delete.</param>
        /// <returns>The number of rows deleted.</returns>
        Task<int> DeleteFiredTriggerAsync(ConnectionAndTransactionHolder conn, string entryId);

        /// <summary>
        /// Get the number instances of the identified job currently executing.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="jobKey">The key identifying the job.</param>
        /// <returns>
        /// The number instances of the identified job currently executing.
        /// </returns>
        Task<int> SelectJobExecutionCountAsync(ConnectionAndTransactionHolder conn, JobKey jobKey);

        /// <summary>
        /// Insert a scheduler-instance state record.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="instanceId">The instance id.</param>
        /// <param name="checkInTime">The check in time.</param>
        /// <param name="interval">The interval.</param>
        /// <returns>The number of inserted rows.</returns>
        Task<int> InsertSchedulerStateAsync(ConnectionAndTransactionHolder conn, string instanceId, DateTimeOffset checkInTime, TimeSpan interval);

        /// <summary>
        /// Delete a scheduler-instance state record.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="instanceId">The instance id.</param>
        /// <returns>The number of deleted rows.</returns>
        Task<int> DeleteSchedulerStateAsync(ConnectionAndTransactionHolder conn, string instanceId);

        /// <summary>
        /// Update a scheduler-instance state record.
        /// </summary>
        /// <param name="conn">The DB Connection</param>
        /// <param name="instanceId">The instance id.</param>
        /// <param name="checkInTime">The check in time.</param>
        /// <returns>The number of updated rows.</returns>
        Task<int> UpdateSchedulerStateAsync(ConnectionAndTransactionHolder conn, string instanceId, DateTimeOffset checkInTime);

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
        Task<IReadOnlyList<SchedulerStateRecord>> SelectSchedulerStateRecordsAsync(ConnectionAndTransactionHolder conn, string instanceName);

        /// <summary>
        /// Select the next trigger which will fire to fire between the two given timestamps 
        /// in ascending order of fire time, and then descending by priority.
        /// </summary>
        /// <param name="conn">The conn.</param>
        /// <param name="noLaterThan">highest value of <see cref="ITrigger.GetNextFireTimeUtc" /> of the triggers (exclusive)</param>
        /// <param name="noEarlierThan">highest value of <see cref="ITrigger.GetNextFireTimeUtc" /> of the triggers (inclusive)</param>
        /// <param name="maxCount">maximum number of trigger keys allow to acquired in the returning list.</param>
        /// <returns>A (never null, possibly empty) list of the identifiers (Key objects) of the next triggers to be fired.</returns>
        Task<IReadOnlyList<TriggerKey>> SelectTriggerToAcquireAsync(ConnectionAndTransactionHolder conn, DateTimeOffset noLaterThan, DateTimeOffset noEarlierThan, int maxCount);

        /// <summary>
        /// Select the distinct instance names of all fired-trigger records.
        /// </summary>
        /// <remarks>
        /// This is useful when trying to identify orphaned fired triggers (a 
        /// fired trigger without a scheduler state record.) 
        /// </remarks>
        /// <param name="conn">The conn.</param>
        /// <returns></returns>
        Task<ISet<string>> SelectFiredTriggerInstanceNamesAsync(ConnectionAndTransactionHolder conn);

        /// <summary>
        /// Counts the misfired triggers in states.
        /// </summary>
        /// <param name="conn">The conn.</param>
        /// <param name="state1">The state1.</param>
        /// <param name="ts">The ts.</param>
        /// <returns></returns>
        Task<int> CountMisfiredTriggersInStateAsync(ConnectionAndTransactionHolder conn, string state1, DateTimeOffset ts);

        /// <summary>
        /// Selects the misfired triggers in states.
        /// </summary>
        /// <param name="conn">The conn.</param>
        /// <param name="state1">The state1.</param>
        /// <param name="ts">The ts.</param>
        /// <param name="count">The count.</param>
        /// <param name="resultList">The result list.</param>
        /// <returns></returns>
        Task<bool> HasMisfiredTriggersInStateAsync(ConnectionAndTransactionHolder conn, string state1, DateTimeOffset ts, int count, IList<TriggerKey> resultList);

        Task<int> UpdateFiredTriggerAsync(ConnectionAndTransactionHolder conn, IOperableTrigger trigger, string state, IJobDetail job);

        /// <summary>
        /// Clear (delete!) all scheduling data - all <see cref="IJob"/>s, <see cref="ITrigger" />s
        /// <see cref="ICalendar" />s.
        /// </summary>
        /// <param name="conn"></param>
        Task ClearDataAsync(ConnectionAndTransactionHolder conn);
    }
}