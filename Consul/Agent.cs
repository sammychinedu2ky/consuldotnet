// -----------------------------------------------------------------------
//  <copyright file="Agent.cs" company="PlayFab Inc">
//    Copyright 2015 PlayFab Inc.
//    Copyright 2020 G-Research Limited
//
//    Licensed under the Apache License, Version 2.0 (the "License");
//    you may not use this file except in compliance with the License.
//    You may obtain a copy of the License at
//
//        http://www.apache.org/licenses/LICENSE-2.0
//
//    Unless required by applicable law or agreed to in writing, software
//    distributed under the License is distributed on an "AS IS" BASIS,
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//    See the License for the specific language governing permissions and
//    limitations under the License.
//  </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Consul.Filtering;
using Newtonsoft.Json;

namespace Consul
{
    /// <summary>
    /// The status of a TTL check
    /// </summary>
    public class TTLStatus : IEquatable<TTLStatus>
    {
        public string Status { get; private set; }
        internal string LegacyStatus { get; private set; }

        public static TTLStatus Pass { get; } = new TTLStatus() { Status = "passing", LegacyStatus = "pass" };

        public static TTLStatus Warn { get; } = new TTLStatus() { Status = "warning", LegacyStatus = "warn" };

        public static TTLStatus Critical { get; } = new TTLStatus() { Status = "critical", LegacyStatus = "fail" };

        [Obsolete("Use TTLStatus.Critical instead. This status will be an error in 0.7.0+", true)]
        public static TTLStatus Fail => Critical;

        public bool Equals(TTLStatus other)
        {
            return other != null && ReferenceEquals(this, other);
        }

        public override bool Equals(object other)
        {
            // other could be a reference type, the is operator will return false if null
            return other != null &&
                   GetType() == other.GetType() &&
                   Equals((TTLStatus)other);
        }

        public override int GetHashCode()
        {
            return Status.GetHashCode();
        }
    }

    /// <summary>
    /// TLS Status Convertor (to and from JSON)
    /// </summary>
    public class TTLStatusConverter : JsonConverter
    {
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            serializer.Serialize(writer, ((TTLStatus)value).Status);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue,
            JsonSerializer serializer)
        {
            var status = (string)serializer.Deserialize(reader, typeof(string));
            switch (status)
            {
                case "pass":
                    return TTLStatus.Pass;
                case "passing":
                    return TTLStatus.Pass;
                case "warn":
                    return TTLStatus.Warn;
                case "warning":
                    return TTLStatus.Warn;
                case "fail":
                    return TTLStatus.Critical;
                case "critical":
                    return TTLStatus.Critical;
                default:
                    throw new ArgumentException("Invalid TTL status value during deserialization");
            }
        }

        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(TTLStatus);
        }
    }

    /// <summary>
    /// AgentCheck represents a check known to the agent
    /// </summary>
    public class AgentCheck
    {
        public string Node { get; set; }
        public string CheckID { get; set; }
        public string Name { get; set; }

        [JsonConverter(typeof(HealthStatusConverter))]
        public HealthStatus Status { get; set; }

        public string Notes { get; set; }
        public string Output { get; set; }
        public string ServiceID { get; set; }
        public string ServiceName { get; set; }
        public string Type { get; set; }
    }

    /// <summary>
    /// AgentService represents a service known to the agent
    /// </summary>
    public class AgentService
    {
        public string ID { get; set; }
        public string Service { get; set; }
        public string[] Tags { get; set; }
        public int Port { get; set; }
        public string Address { get; set; }

        public IDictionary<string, ServiceTaggedAddress> TaggedAddresses { get; set; }
        public bool EnableTagOverride { get; set; }
        public IDictionary<string, string> Meta { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] // If the Proxy property is serialized to have null value, a protocol error occurs when registering the service through the catalog (catalog/register) during an http request.
        public AgentServiceProxy Proxy { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public ServiceKind Kind { get; set; }
    }

    /// <summary>
    /// ServiceKind specifies the type of service.
    /// </summary>
    [TypeConverter(typeof(ServiceKindTypeConverter))]
    public class ServiceKind : IEquatable<ServiceKind>
    {
        static IReadOnlyDictionary<string, ServiceKind> Map { get; } = new Dictionary<string, ServiceKind>(StringComparer.OrdinalIgnoreCase)
        {
            { "connect-proxy", new ServiceKind("connect-proxy") },
            { "mesh-gateway", new ServiceKind("mesh-gateway") },
            { "terminating-gateway", new ServiceKind("terminating-gateway") },
            { "ingress-gateway", new ServiceKind("ingress-gateway") },
        };

        public static ServiceKind ConnectProxy => Map["connect-proxy"];
        public static ServiceKind MeshGateway => Map["mesh-gateway"];
        public static ServiceKind TerminatingGateway => Map["terminating-gateway"];
        public static ServiceKind IngressGateway => Map["ingress-gateway"];

        string Value { get; }

        ServiceKind(string value) => Value = value;

        public override bool Equals(object obj) => obj is ServiceKind typedObject ? Equals(typedObject) : Value.Equals(obj.ToString(), StringComparison.OrdinalIgnoreCase);

        public bool Equals(ServiceKind other) => ReferenceEquals(this, other);

        public override int GetHashCode() => Value.GetHashCode();

        public override string ToString() => Value.ToString();

        public static bool TryParse(string value, out ServiceKind result)
        {
            result = null;

            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            return Map.TryGetValue(value, out result);
        }
    }

    class ServiceKindTypeConverter : TypeConverter
    {
        public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType) => sourceType == typeof(string);
        public override bool CanConvertTo(ITypeDescriptorContext context, Type destinationType) => destinationType == typeof(string);
        public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value) => ServiceKind.TryParse(value?.ToString(), out ServiceKind result) ? result : throw new NotSupportedException();
        public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType) => value is null ? string.Empty : value.ToString();
    }

    /// <summary>
    /// AgentMember represents a cluster member known to the agent
    /// </summary>
    public class AgentMember
    {
        public string Name { get; set; }
        public string Addr { get; set; }
        public ushort Port { get; set; }
        public Dictionary<string, string> Tags { get; set; }
        public int Status { get; set; }
        public byte ProtocolMin { get; set; }
        public byte ProtocolMax { get; set; }
        public byte ProtocolCur { get; set; }
        public byte DelegateMin { get; set; }
        public byte DelegateMax { get; set; }
        public byte DelegateCur { get; set; }

        public AgentMember()
        {
            Tags = new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// AgentServiceRegistration is used to register a new service
    /// </summary>
    public class AgentServiceRegistration
    {
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string ID { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string Name { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string[] Tags { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public int Port { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string Address { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public bool EnableTagOverride { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public AgentServiceCheck Check { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public AgentServiceCheck[] Checks { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public IDictionary<string, string> Meta { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public IDictionary<string, ServiceTaggedAddress> TaggedAddresses { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public AgentServiceConnect Connect { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public AgentServiceProxy Proxy { get; set; }
    }

    /// <summary>
    /// AgentCheckRegistration is used to register a new check
    /// </summary>
    public class AgentCheckRegistration : AgentServiceCheck
    {
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string ServiceID { get; set; }
    }

    /// <summary>
    /// AgentServiceConnect specifies the configuration for Connect
    /// </summary>
    public class AgentServiceConnect
    {
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public AgentServiceRegistration SidecarService { get; set; }
    }

    /// <summary>
    /// AgentServiceProxy specifies the configuration for a Connect service proxy instance. This is only valid if Kind defines a proxy or gateway.
    /// </summary>
    public class AgentServiceProxy
    {
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string DestinationServiceID { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public int LocalServicePort { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string LocalServiceAddress { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string DestinationServiceName { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public AgentServiceProxyUpstream[] Upstreams { get; set; }
    }

    /// <summary>
    /// AgentServiceProxyUpstream specifies the upstream service for which the proxy should create a listener.
    /// </summary>
    public class AgentServiceProxyUpstream
    {
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string DestinationName { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public int LocalBindPort { get; set; }
    }

    /// <summary>
    /// AgentServiceCheck is used to create an associated check for a service
    /// </summary>
    public class AgentServiceCheck
    {
        // See https://github.com/G-Research/consuldotnet/issues/184
        [Obsolete("Use CheckId instead")]
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string ID { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string CheckID { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string Name { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string Notes { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string Script { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string[] Args { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string DockerContainerID { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string Shell { get; set; } // Only supported for Docker.

        [JsonConverter(typeof(DurationTimespanConverter))]
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public TimeSpan? Interval { get; set; }

        [JsonConverter(typeof(DurationTimespanConverter))]
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public TimeSpan? Timeout { get; set; }

        [JsonConverter(typeof(DurationTimespanConverter))]
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public TimeSpan? TTL { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string HTTP { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, List<string>> Header { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string Method { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string Body { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string TCP { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        [JsonConverter(typeof(HealthStatusConverter))]
        public HealthStatus Status { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public bool TLSSkipVerify { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string GRPC { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public bool GRPCUseTLS { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string AliasService { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string AliasNode { get; set; }

        /// <summary>
        /// In Consul 0.7 and later, checks that are associated with a service
        /// may also contain this optional DeregisterCriticalServiceAfter field,
        /// which is a timeout in the same Go time format as Interval and TTL. If
        /// a check is in the critical state for more than this configured value,
        /// then its associated service (and all of its associated checks) will
        /// automatically be deregistered.
        /// </summary>
        [JsonConverter(typeof(DurationTimespanConverter))]
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public TimeSpan? DeregisterCriticalServiceAfter { get; set; }
    }


    /// <summary>
    /// LocalServiceHealth represents the health of a service and its associated checks as returned by the Agent API
    /// </summary>
    public class LocalServiceHealth
    {
        [JsonConverter(typeof(HealthStatusConverter))]
        public HealthStatus AggregatedStatus { get; set; }

        public AgentService Service { get; set; }

        public AgentCheck[] Checks { get; set; }
    }

    /// <summary>
    /// AgentVersion represents the version information for the Consul agent
    /// </summary>
    public class AgentVersion
    {
        public string SHA { get; set; }
        public DateTime BuildDate { get; set; }
        public string HumanVersion { get; set; }
        public string FIPS { get; set; }
    }
    /// <summary>
    /// Log Level Enum
    /// </summary>
    public enum LogLevel
    {
        Info,
        Trace,
        Debug,
        Warn,
        Err
    }

    /// <summary>
    /// Agent can be used to query the Agent endpoints
    /// </summary>
    public class Agent : IAgentEndpoint
    {
        private class CheckUpdate
        {
            public string Status { get; set; }
            public string Output { get; set; }
        }
        private readonly ConsulClient _client;
        private string _nodeName;
        private readonly AsyncLock _nodeNameLock;

        internal Agent(ConsulClient c)
        {
            _client = c;
            _nodeNameLock = new AsyncLock();
        }

        /// <summary>
        /// Self is used to query the agent we are speaking to for information about itself
        /// </summary>
        /// <returns>A somewhat dynamic object representing the various data elements in Self</returns>
        public Task<QueryResult<Dictionary<string, Dictionary<string, dynamic>>>> Self(CancellationToken ct = default)
        {
            return _client.Get<Dictionary<string, Dictionary<string, dynamic>>>("/v1/agent/self").Execute(ct);
        }

        /// <summary>
        /// NodeName is used to get the node name of the agent
        /// </summary>
        [Obsolete("This property will be removed in a future version. Replace uses of it with a call to 'await GetNodeName()'")]
        public string NodeName => GetNodeName().ConfigureAwait(false).GetAwaiter().GetResult();

        /// <summary>
        /// GetNodeName is used to get the node name of the agent. The value is cached per instance of ConsulClient after the first use.
        /// </summary>
        public async Task<string> GetNodeName(CancellationToken ct = default)
        {
            if (_nodeName == null)
            {
                using (await _nodeNameLock.LockAsync().ConfigureAwait(false))
                {
                    if (_nodeName == null)
                    {
                        _nodeName = (await Self(ct).ConfigureAwait(false)).Response["Config"]["NodeName"];
                    }
                }
            }

            return _nodeName;
        }

        /// <summary>
        /// Checks returns the locally registered checks
        /// </summary>
        /// <returns>A map of the registered check names and check data</returns>
        public Task<QueryResult<Dictionary<string, AgentCheck>>> Checks(CancellationToken ct = default)
        {
            return Checks(null, ct);
        }

        /// <summary>
        /// Checks returns the locally registered checks
        /// </summary>
        /// <param name="filter">Specifies the expression used to filter the queries results prior to returning the data</param>
        /// <returns>A map of the registered check names and check data</returns>
        public Task<QueryResult<Dictionary<string, AgentCheck>>> Checks(Filter filter, CancellationToken ct = default)
        {
            return _client.Get<Dictionary<string, AgentCheck>>("/v1/agent/checks", filter: filter).Execute(ct);
        }

        /// <summary>
        /// Services returns the locally registered services
        /// </summary>
        /// <returns>A map of the registered services and service data</returns>
        public async Task<QueryResult<Dictionary<string, AgentService>>> Services(CancellationToken ct = default)
        {
            return await Services(null, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Services returns the locally registered services
        /// </summary>
        /// <param name="filter">Specifies the expression used to filter the queries results prior to returning the data</param>
        /// <returns>A map of the registered services and service data</returns>
        public async Task<QueryResult<Dictionary<string, AgentService>>> Services(Filter filter, CancellationToken ct = default)
        {
            return await _client.Get<Dictionary<string, AgentService>>("/v1/agent/services", null, filter).Execute(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Members returns the known gossip members. The WAN flag can be used to query a server for WAN members.
        /// </summary>
        /// <returns>An array of gossip peers</returns>
        public Task<QueryResult<AgentMember[]>> Members(bool wan, CancellationToken ct = default)
        {
            var req = _client.Get<AgentMember[]>("/v1/agent/members");
            if (wan)
            {
                req.Params["wan"] = "1";
            }
            return req.Execute(ct);
        }

        /// <summary>
        /// ServiceRegister is used to register a new service with the local agent
        /// </summary>
        /// <param name="service">A service registration object</param>
        /// <returns>An empty write result</returns>
        public Task<WriteResult> ServiceRegister(AgentServiceRegistration service, CancellationToken ct = default)
        {
            return ServiceRegister(service, replaceExistingChecks: false, ct);
        }

        /// <summary>
        /// ServiceRegister is used to register a new service with the local agent
        /// </summary>
        /// <param name="service">A service registration object</param>
        /// <param name="replaceExistingChecks">Missing health checks from the request will be deleted from the agent.</param>
        /// <returns>An empty write result</returns>
        public Task<WriteResult> ServiceRegister(AgentServiceRegistration service, bool replaceExistingChecks, CancellationToken ct = default)
        {
            var req = _client.Put("/v1/agent/service/register", service, null);
            if (replaceExistingChecks)
            {
                req.Params["replace-existing-checks"] = "true";
            }
            return req.Execute(ct);
        }

        /// <summary>
        /// ServiceRegister is used to register a new service with the local agent
        /// </summary>
        /// <param name="serviceID">The service ID</param>
        /// <returns>An empty write result</returns>
        public Task<WriteResult> ServiceDeregister(string serviceID, CancellationToken ct = default)
        {
            return _client.PutNothing(string.Format("/v1/agent/service/deregister/{0}", serviceID)).Execute(ct);
        }

        /// <summary>
        /// PassTTL is used to set a TTL check to the passing state
        /// </summary>
        /// <param name="checkID">The check ID</param>
        /// <param name="note">An optional, arbitrary string to write to the check status</param>
        public Task PassTTL(string checkID, string note, CancellationToken ct = default)
        {
            return LegacyUpdateTTL(checkID, note, TTLStatus.Pass, ct);
        }

        /// <summary>
        /// WarnTTL is used to set a TTL check to the warning state
        /// </summary>
        /// <param name="checkID">The check ID</param>
        /// <param name="note">An optional, arbitrary string to write to the check status</param>
        public Task WarnTTL(string checkID, string note, CancellationToken ct = default)
        {
            return LegacyUpdateTTL(checkID, note, TTLStatus.Warn, ct);
        }

        /// <summary>
        /// FailTTL is used to set a TTL check to the failing state
        /// </summary>
        /// <param name="checkID">The check ID</param>
        /// <param name="note">An optional, arbitrary string to write to the check status</param>
        public Task FailTTL(string checkID, string note, CancellationToken ct = default)
        {
            return LegacyUpdateTTL(checkID, note, TTLStatus.Critical, ct);
        }

        /// <summary>
        /// UpdateTTL is used to update the TTL of a check
        /// </summary>
        /// <param name="checkID">The check ID</param>
        /// <param name="output">An optional, arbitrary string to write to the check status</param>
        /// <param name="status">The state to set the check to</param>
        /// <returns>An empty write result</returns>
        public Task<WriteResult> UpdateTTL(string checkID, string output, TTLStatus status, CancellationToken ct = default)
        {
            var u = new CheckUpdate
            {
                Status = status.Status,
                Output = output
            };
            return _client.Put(string.Format("/v1/agent/check/update/{0}", checkID), u, null).Execute(ct);
        }

        /// <summary>
        /// LegacyUpdateTTL is used to update the TTL of a check
        /// </summary>
        /// <param name="checkID">The check ID</param>
        /// <param name="note">An optional, arbitrary string to note on the check status</param>
        /// <param name="status">The state to set the check to</param>
        /// <returns>An empty write result</returns>
        private Task<WriteResult> LegacyUpdateTTL(string checkID, string note, TTLStatus status, CancellationToken ct = default)
        {
            var request = _client.PutNothing(string.Format("/v1/agent/check/{0}/{1}", status.LegacyStatus, checkID));
            if (!string.IsNullOrEmpty(note))
            {
                request.Params.Add("note", note);
            }
            return request.Execute(ct);
        }

        /// <summary>
        /// CheckRegister is used to register a new check with the local agent
        /// </summary>
        /// <param name="check">A check registration object</param>
        /// <returns>An empty write result</returns>
        public Task<WriteResult> CheckRegister(AgentCheckRegistration check, CancellationToken ct = default)
        {
            return _client.Put("/v1/agent/check/register", check, null).Execute(ct);
        }

        /// <summary>
        /// CheckDeregister is used to deregister a check with the local agent
        /// </summary>
        /// <param name="checkID">The check ID to deregister</param>
        /// <returns>An empty write result</returns>
        public Task<WriteResult> CheckDeregister(string checkID, CancellationToken ct = default)
        {
            return _client.PutNothing(string.Format("/v1/agent/check/deregister/{0}", checkID)).Execute(ct);
        }

        /// <summary>
        /// Join is used to instruct the agent to attempt a join to another cluster member
        /// </summary>
        /// <param name="addr">The address to join to</param>
        /// <param name="wan">Join the WAN pool</param>
        /// <returns>An empty write result</returns>
        public Task<WriteResult> Join(string addr, bool wan, CancellationToken ct = default)
        {
            var req = _client.PutNothing(string.Format("/v1/agent/join/{0}", addr));
            if (wan)
            {
                req.Params["wan"] = "1";
            }
            return req.Execute(ct);
        }

        /// <summary>
        /// ForceLeave is used to have the agent eject a failed node
        /// </summary>
        /// <param name="node">The node name to remove</param>
        /// <returns>An empty write result</returns>
        public Task<WriteResult> ForceLeave(string node, CancellationToken ct = default)
        {
            return _client.PutNothing(string.Format("/v1/agent/force-leave/{0}", node)).Execute(ct);
        }

        /// <summary>
        /// Leave is used to have the agent gracefully leave the cluster and shutdown
        /// </summary>
        /// <returns>An empty write result</returns>
        public Task<WriteResult> Leave(string node, CancellationToken ct = default)
        {
            return _client.PutNothing("/v1/agent/leave").Execute(ct);
        }

        /// <summary>
        /// Reload triggers a configuration reload for the agent we are connected to.
        /// </summary>
        /// <returns>An empty write result</returns>
        public Task<WriteResult> Reload(string node, CancellationToken ct = default)
        {
            return _client.PutNothing("/v1/agent/reload").Execute(ct);
        }

        /// <summary>
        /// EnableServiceMaintenance toggles service maintenance mode on for the given service ID
        /// </summary>
        /// <param name="serviceID">The service ID</param>
        /// <param name="reason">An optional reason</param>
        /// <returns>An empty write result</returns>
        public Task<WriteResult> EnableServiceMaintenance(string serviceID, string reason, CancellationToken ct = default)
        {
            var req = _client.PutNothing(string.Format("/v1/agent/service/maintenance/{0}", serviceID));
            req.Params["enable"] = "true";
            req.Params["reason"] = reason;
            return req.Execute(ct);
        }

        /// <summary>
        /// DisableServiceMaintenance toggles service maintenance mode off for the given service ID
        /// </summary>
        /// <param name="serviceID">The service ID</param>
        /// <returns>An empty write result</returns>
        public Task<WriteResult> DisableServiceMaintenance(string serviceID, CancellationToken ct = default)
        {
            var req = _client.PutNothing(string.Format("/v1/agent/service/maintenance/{0}", serviceID));
            req.Params["enable"] = "false";
            return req.Execute(ct);
        }

        /// <summary>
        /// EnableNodeMaintenance toggles node maintenance mode on for the agent we are connected to
        /// </summary>
        /// <param name="reason">An optional reason</param>
        /// <returns>An empty write result</returns>
        public Task<WriteResult> EnableNodeMaintenance(string reason, CancellationToken ct = default)
        {
            var req = _client.PutNothing("/v1/agent/maintenance");
            req.Params["enable"] = "true";
            req.Params["reason"] = reason;
            return req.Execute(ct);
        }

        /// <summary>
        /// DisableNodeMaintenance toggles node maintenance mode off for the agent we are connected to
        /// </summary>
        /// <returns>An empty write result</returns>
        public Task<WriteResult> DisableNodeMaintenance(CancellationToken ct = default)
        {
            var req = _client.PutNothing("/v1/agent/maintenance");
            req.Params["enable"] = "false";
            return req.Execute(ct);
        }

        /// <summary>
        /// Monitor yields log lines to display streaming logs from the agent
        /// Providing a CancellationToken can be used to close the connection and stop the
        /// log stream, otherwise the log stream will time out based on the HTTP Client's timeout value.
        /// </summary>
        public async Task<LogStream> Monitor(LogLevel level = default, CancellationToken ct = default)
        {
            var req = _client.Get<Stream>("/v1/agent/monitor");
            req.Params["loglevel"] = level.ToString().ToLowerInvariant();

            var res = await req.ExecuteStreaming(ct).ConfigureAwait(false);
            return new LogStream(res.Response);
        }

        /// <summary>
        /// MonitorJSON is like Monitor except it returns logs in JSON format.
        /// </summary>
        public async Task<LogStream> MonitorJSON(LogLevel level = default, CancellationToken ct = default)
        {
            var req = _client.Get<Stream>("/v1/agent/monitor");
            req.Params["loglevel"] = level.ToString().ToLowerInvariant();
            req.Params["logjson"] = "true";

            var res = await req.ExecuteStreaming(ct).ConfigureAwait(false);
            return new LogStream(res.Response);
        }

        /// <summary>
        /// GetLocalServiceHealth returns the health info of a service registered on the local agent
        /// </summary>
        /// <param name="serviceName">Name of service</param>
        /// <returns>An array containing the details of each passing, warning, or critical service</returns>
        public async Task<QueryResult<LocalServiceHealth[]>> GetLocalServiceHealth(string serviceName, QueryOptions q, CancellationToken ct = default)
        {
            return await _client.Get<LocalServiceHealth[]>($"v1/agent/health/service/name/{serviceName}", q).Execute(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// GetLocalServiceHealth returns the health info of a service registered on the local agent
        /// </summary>
        /// <param name="serviceName">Name of service</param>
        /// <returns>An array containing the details of each passing, warning, or critical service</returns>
        public async Task<QueryResult<LocalServiceHealth[]>> GetLocalServiceHealth(string serviceName, CancellationToken ct = default)
        {
            return await GetLocalServiceHealth(serviceName, QueryOptions.Default, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// GetWorstLocalServiceHealth returns the worst aggregated status of a service registered on the local agent
        /// </summary>
        /// <param name="serviceName">Name of service</param>
        /// <returns>passing, warning, or critical</returns>
        public async Task<QueryResult<string>> GetWorstLocalServiceHealth(string serviceName, QueryOptions q, CancellationToken ct = default)
        {
            var req = _client.Get($"v1/agent/health/service/name/{serviceName}", q);
            req.Params["format"] = "text";
            return await req.Execute(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// GetWorstLocalServiceHealth returns the worst aggregated status of a service registered on the local agent
        /// </summary>
        /// <param name="serviceName">Name of service</param>
        /// <returns>passing, warning, or critical</returns>
        public async Task<QueryResult<string>> GetWorstLocalServiceHealth(string serviceName, CancellationToken ct = default)
        {
            return await GetWorstLocalServiceHealth(serviceName, QueryOptions.Default, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// GetLocalServiceHealthByID returns the health info of a service registered on the local agent by ID
        /// </summary>
        /// <param name="serviceID">ID of the service</param>
        /// <returns>An array containing the details of each passing, warning, or critical service</returns>
        public async Task<QueryResult<LocalServiceHealth>> GetLocalServiceHealthByID(string serviceID, QueryOptions q, CancellationToken ct = default)
        {
            return await _client.Get<LocalServiceHealth>($"v1/agent/health/service/id/{serviceID}", q).Execute(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// GetLocalServiceHealthByID returns the health info of a service registered on the local agent by ID
        /// </summary>
        /// <param name="serviceID">ID of the service</param>
        /// <returns>An array containing the details of each passing, warning, or critical service</returns>
        public async Task<QueryResult<LocalServiceHealth>> GetLocalServiceHealthByID(string serviceID, CancellationToken ct = default)
        {
            return await GetLocalServiceHealthByID(serviceID, QueryOptions.Default, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// GetAgentVersion returns the version of the agent
        /// </summary>
        /// <param name="ct"></param>
        /// <returns>Version of the agent</returns>
        public async Task<QueryResult<AgentVersion>> GetAgentVersion(CancellationToken ct = default)
        {
            return await _client.Get<AgentVersion>("/v1/agent/version").Execute(ct).ConfigureAwait(false);
        }   
        /// <summary>
        /// Log streamer
        /// </summary>
        public class LogStream : IEnumerable<Task<string>>, IDisposable
        {
            private readonly Stream _stream;
            private readonly StreamReader _streamreader;
            internal LogStream(Stream s)
            {
                _stream = s;
                _streamreader = new StreamReader(s);
            }

            public void Dispose()
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }

            public virtual void Dispose(bool disposing)
            {
                if (!disposing) return;
                _streamreader.Dispose();
                _stream.Dispose();
            }

            public IEnumerator<Task<string>> GetEnumerator()
            {

                while (!_streamreader.EndOfStream)
                {
                    yield return _streamreader.ReadLineAsync();
                }
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }
    }

    public partial class ConsulClient : IConsulClient
    {
        private Lazy<Agent> _agent;

        /// <summary>
        /// Agent returns a handle to the agent endpoints
        /// </summary>
        public IAgentEndpoint Agent => _agent.Value;
    }
}
