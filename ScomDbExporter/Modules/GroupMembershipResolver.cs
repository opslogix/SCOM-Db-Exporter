using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using ScomDbExporter.Config;

namespace ScomDbExporter.Modules
{
    /// <summary>
    /// Resolves SCOM group display names to the set of BaseManagedEntityIds
    /// that belong to those groups (directly, transitively via nested groups,
    /// and optionally via hosting relationships).
    /// </summary>
    internal sealed class GroupMembershipResolver
    {
        private readonly string _connString;
        private readonly GroupResolverConfig _config;
        private readonly ILogger<GroupMembershipResolver> _log;
        private readonly List<string> _allConfiguredNames;

        private readonly object _lock = new();
        private Dictionary<string, HashSet<Guid>> _membersByGroup =
            new(StringComparer.OrdinalIgnoreCase);

        private DateTime _nextRefreshUtc = DateTime.MinValue;

        public DateTime LastRefreshUtc { get; private set; }
        public bool HasAnyGroups => _allConfiguredNames.Count > 0;

        public GroupMembershipResolver(
            string connString,
            GroupResolverConfig config,
            IEnumerable<string> allConfiguredGroupNames,
            ILogger<GroupMembershipResolver> log)
        {
            if (string.IsNullOrWhiteSpace(connString))
                throw new ArgumentException("Connection string is null or empty", nameof(connString));

            _connString = connString;
            _config = config ?? new GroupResolverConfig();
            _log = log;

            var dedup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (allConfiguredGroupNames != null)
            {
                foreach (var name in allConfiguredGroupNames)
                {
                    if (!string.IsNullOrWhiteSpace(name))
                        dedup.Add(name.Trim());
                }
            }
            _allConfiguredNames = new List<string>(dedup);
        }

        public void Init()
        {
            if (!HasAnyGroups)
            {
                _log.LogInformation("Group resolver: no groups configured — filtering disabled");
                return;
            }

            _log.LogInformation(
                "Group resolver initialising for {Count} group(s): {Groups}",
                _allConfiguredNames.Count, string.Join(", ", _allConfiguredNames));

            Refresh();
        }

        public void Tick()
        {
            if (!HasAnyGroups) return;
            if (DateTime.UtcNow < _nextRefreshUtc) return;
            Refresh();
        }

        /// <summary>
        /// Snapshot of allowed BME IDs for the union of the given group names.
        /// Returns null if <paramref name="groupNames"/> is null/empty (caller should
        /// short-circuit and apply no filter).
        /// </summary>
        public HashSet<Guid> GetAllowedBmes(string[] groupNames)
        {
            if (groupNames == null || groupNames.Length == 0)
                return null;

            lock (_lock)
            {
                var union = new HashSet<Guid>();
                foreach (var name in groupNames)
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    if (_membersByGroup.TryGetValue(name.Trim(), out var set))
                        union.UnionWith(set);
                }
                return union;
            }
        }

        private void Refresh()
        {
            _nextRefreshUtc = DateTime.UtcNow.AddMinutes(Math.Max(1, _config.RefreshMinutes));

            var sw = Stopwatch.StartNew();
            var newMap = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in _allConfiguredNames)
                newMap[name] = new HashSet<Guid>();

            string sql = BuildSql();
            int rowCount = 0;

            try
            {
                using var conn = new SqlConnection(_connString);
                using var cmd = new SqlCommand(sql, conn);

                for (int i = 0; i < _allConfiguredNames.Count; i++)
                    cmd.Parameters.AddWithValue("@g" + i, _allConfiguredNames[i]);

                cmd.Parameters.AddWithValue("@MaxDepth", Math.Max(1, _config.MaxNestedDepth));

                conn.Open();
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    rowCount++;
                    var groupName = r.GetString(0);
                    var bmeId = r.GetGuid(1);

                    if (newMap.TryGetValue(groupName, out var set))
                        set.Add(bmeId);
                }
            }
            catch (SqlException ex)
            {
                _log.LogError(ex,
                    "Group resolver SQL failed after {ElapsedMs}ms — keeping previous snapshot",
                    sw.ElapsedMilliseconds);
                return;
            }

            lock (_lock)
            {
                _membersByGroup = newMap;
                LastRefreshUtc = DateTime.UtcNow;
            }

            int totalDistinct = 0;
            var perGroupSummary = new StringBuilder();
            foreach (var kv in newMap)
            {
                totalDistinct += kv.Value.Count;
                if (perGroupSummary.Length > 0) perGroupSummary.Append(", ");
                perGroupSummary.Append(kv.Key).Append("=").Append(kv.Value.Count);

                if (kv.Value.Count == 0)
                {
                    _log.LogWarning(
                        "Group '{Group}' resolved to 0 entities — check the display name or membership",
                        kv.Key);
                }
            }

            _log.LogInformation(
                "Group resolver refresh complete: {Rows} rows, {Distinct} memberships ({PerGroup}) in {ElapsedMs}ms (next in {RefreshMin} min)",
                rowCount, totalDistinct, perGroupSummary, sw.ElapsedMilliseconds, _config.RefreshMinutes);
        }

        private string BuildSql()
        {
            var paramList = new List<string>(_allConfiguredNames.Count);
            for (int i = 0; i < _allConfiguredNames.Count; i++)
                paramList.Add("@g" + i);

            var sb = new StringBuilder();
            sb.Append(@"
WITH RootGroups AS (
    SELECT BaseManagedEntityId, DisplayName
    FROM dbo.BaseManagedEntity WITH (NOLOCK)
    WHERE IsDeleted = 0 AND DisplayName IN (");
            sb.Append(string.Join(", ", paramList));
            sb.Append(@")
),
NestedGroups AS (
    SELECT rg.BaseManagedEntityId, rg.DisplayName AS GroupName, 0 AS Depth
    FROM RootGroups rg
    UNION ALL
    SELECT r.TargetEntityId, ng.GroupName, ng.Depth + 1
    FROM NestedGroups ng
    JOIN dbo.Relationship r WITH (NOLOCK)
        ON r.SourceEntityId = ng.BaseManagedEntityId
       AND r.IsDeleted = 0
    JOIN dbo.MTV_Group mg WITH (NOLOCK)
        ON mg.BaseManagedEntityId = r.TargetEntityId
    WHERE ng.Depth < @MaxDepth
),
DirectMembers AS (
    SELECT DISTINCT ng.GroupName, r.TargetEntityId AS BmeId
    FROM NestedGroups ng
    JOIN dbo.Relationship r WITH (NOLOCK)
        ON r.SourceEntityId = ng.BaseManagedEntityId
       AND r.IsDeleted = 0
    JOIN dbo.BaseManagedEntity tbme WITH (NOLOCK)
        ON tbme.BaseManagedEntityId = r.TargetEntityId
       AND tbme.IsDeleted = 0
)
SELECT GroupName, BmeId FROM DirectMembers");

            if (_config.IncludeHostedChildren)
            {
                sb.Append(@"
UNION
SELECT dm.GroupName, hosted.BaseManagedEntityId
FROM DirectMembers dm
JOIN dbo.BaseManagedEntity hosted WITH (NOLOCK)
    ON hosted.TopLevelHostEntityId = dm.BmeId
   AND hosted.IsDeleted = 0");
            }

            sb.Append(@"
OPTION (MAXRECURSION 32);");
            return sb.ToString();
        }
    }
}
