using Bramki_Notify.ConfigurationQuery;
using Bramki_Notify.EventLogManagement;
using Bramki_Notify.SessionManagement;
using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using System.Threading;
using System.Threading.Tasks;

namespace Bramki_Notify
{
    public enum ApiEventKind { Regular, Denied }

    public sealed record ApiEvent(
        ApiEventKind Kind,
        DateTime Timestamp,
        string Title,
        string PersonLine,
        string? DeniedReason
    );

    public sealed class BramkiApiMonitor : IAsyncDisposable
    {
        public Task MonitoringTask => _loopTask ?? Task.CompletedTask;

        private const int ObjectTypeAccessPoint = 1031;
        // Set the actual controller id
        private const int MonitoredControllerId = 1;

        // AccessPointId -> label type
        private static readonly IReadOnlyDictionary<int, string> AccessPointLabelById =
            // Set the actual access point ids
            new Dictionary<int, string>
            {
                { 1, "Wejście" },
                { 2, "Wejście" },
                { 3, "Wyjście" },
                { 4, "Wyjście" },
                { 5, "Wyjście służbowe" },
                { 6, "Wyjście służbowe" },
            };

        // Denied event codes
        private static readonly HashSet<int> AccessDeniedEventCodes = new()
        {
            630, 604
        };

        // Normal event codes
        private static readonly HashSet<int> RegularEventCodes = new()
        {
            629
        };

        private static readonly IReadOnlyDictionary<int, string> ActionStatusDescriptions =
            new Dictionary<int, string>
            {
                { 0, "OK" },
                { 1, "No authorisation to login on Access Point" },
                { 2, "Authentication Factor: Parse error" },
                { 3, "Nieznana karta" }, // Authentication Factor: Unknown
                { 4, "Niewłaściwa karta" }, // Authentication Factor: Incorrect
                { 5, "Karta dezaktywowana" }, // Authentication Factor: Disabled
                { 6, "Inny proces uwierzytelnienia w trakcie" }, // Another authentication process in progress
                { 7, "Authentication Policy Check: Canceled" },
                { 8, "Authentication Policy Check: Timeout" },
                { 9, "Authentication Policy Check: Failed" },
                { 10, "Uprawnienia dezaktywowane" }, // Access Credential: Disabled
                { 11, "Uprawnienia jeszcze nie aktywne" }, //Access Credential: Not yet active
                { 12, "Uprawnienia wygasły" }, // Access Credential: Expired
                { 13, "Uprawnienia jeszcze nie aktywne" }, // Access Credential: Before valid time
                { 14, "Uprawnienia wygasły" }, // Access Credential: After valid time
                { 15, "Access Point: Temporary blocked due to penalty timer," },
                { 16, "Object controlled by other Input," },
                { 31, "Source object beyond activity schedule," },
                { 32, "Destination object beyond activity schedule," },
                { 33, "Brak wymaganych uprawnień" }, // Authorisation Check: No decision
                { 34, "Authorisation Check: Function Denied," },
                { 35, "Authorisation Check: Access Point Denied," },
                { 36, "Authorisation Check: Place of Action Denied," },
                { 37, "Authorisation Check: Function Parameter Denied," },
                { 41, "No Authorisation for Authentication," },
                { 51, "No Authorisation for Action," },
                { 52, "Access Credential: Over Max Usage Rule," },
                { 53, "Access Credential: Not active," },
                { 54, "Access Credential: Over Max Days Rule," },
                { 55, "Wykryto duplikat karty" }, // Passback rule violation
                { 56, "Wrong Exit Zone" },
                { 57, "Wrong Last Zone" },
                { 58, "Upper Occupancy Limit" },
                { 59, "Lower Occupancy Limit violation" },
                { 60, "Thread too low," },
                { 61, "Access disabled because zone armed," },
                { 62, "No auhorisation for extended lock pulse time," },
                { 63, "Arming disabled," },
                { 64, "Alarm zone already armed (function arming disabled)," },
                { 65, "Alarm zone already armed (function auto-arming delay)," },
                { 66, "Too long time to planned arming," },
                { 67, "Elevator call rejected because zone is armed," },
                { 68, "Access disabled by external signal" },
                { 69, "Floor doesn't exist," },
                { 70, "Access Point not ready" },
                { 71, "Access Door taken by another Access Point," },
                { 72, "Access Credential Awaited Elsewhere," },
                { 91, "No Authorisation for Place of Action," },
                { 92, "Lock disabled," },
                { 101, "Object Set," },
                { 102, "Object Already Set," },
                { 103, "Object Cleared," },
                { 104, "Object Already Cleared," },
                { 111, "Lock Pulse Enable: Normal Pulse," },
                { 112, "Lock Pulse Enable: Extended Pulse," },
                { 113, "Lock Pulse Enable: Unlimited Pulse," },
                { 114, "Lock Pulse Enable: In Pulse," },
                { 115, "Lock Pulse Enable: Delay," },
                { 116, "Lock Pulse Enable: Lock On," },
                { 117, "Lock Pulse Enable: Lock Off," },
                { 118, "Lock Pulse Enable: Conditional Unblocked," },
                { 121, "Automation Node Delay: State changed," },
                { 122, "Automation Node Delay: State not changed," },
                { 123, "Automation Node Set Timed: State changed," },
                { 124, "Automation Node Set Timed: State not changed," },
                { 125, "Automation Node Set Permanent: State changed," },
                { 126, "Automation Node Set Permanent: State not changed," },
                { 127, "Automation Node Clear: State changed," },
                { 128, "Automation Node Clear: State not changed," },
                { 131, "Auto-arming Delay: Zone Armed," },
                { 132, "Auto-arming Delay:Time Long Enough," },
                { 133, "No Auto-arming Delay In Progress," },
                { 134, "Auto-arming Delay: Signaling From Parent," },
                { 135, "Auto-arming Local: Timer Changed," },
                { 136, "Auto-arming Delay: Done" },
            };

        private readonly string _baseUrl;
        private readonly string _login;
        private readonly string _password;

        private SessionManagementServiceClient? _sessionClient;
        private ConfigurationQueryServiceClient? _cfgClient;
        private EventLogManagementServiceClient? _eventLogClient;

        private Guid _sessionToken;
        private int _lastEventId;
        private CancellationTokenSource? _cts;
        private Task? _loopTask;

        private readonly Dictionary<int, string> _personCache = new();

        public event Action<ApiEvent>? EventReceived;
        public event Action<string>? Log;

        public BramkiApiMonitor(string baseUrl, string login, string password)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _login = login ?? "";
            _password = password ?? "";
        }

        public bool IsConnected => _sessionToken != Guid.Empty;

        public async Task ConnectAsync()
        {
            if (IsConnected) return;

            Log?.Invoke("Connecting to API...");

            var sessUrl = $"{_baseUrl}/SessionManagement";
            var cfgUrl = $"{_baseUrl}/ConfigurationQuery";
            var eventUrl = $"{_baseUrl}/EventLogManagement";

            _sessionClient = new SessionManagementServiceClient(
                SessionManagementServiceClient.EndpointConfiguration.BasicHttpBinding_ISessionManagementService,
                sessUrl);

            _cfgClient = new ConfigurationQueryServiceClient(
                ConfigurationQueryServiceClient.EndpointConfiguration.BasicHttpBinding_IConfigurationQueryService,
                cfgUrl);

            var binding = CreateHttpBinding(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
            _eventLogClient = new EventLogManagementServiceClient(binding, new EndpointAddress(eventUrl));
            _eventLogClient.InnerChannel.OperationTimeout = TimeSpan.FromSeconds(20);

            _sessionToken = await _sessionClient.ConnectAsync(_login, _password);

            Log?.Invoke($"Connected. SessionToken={_sessionToken}");
        }

        public async Task StartMonitoringAsync()
        {
            if (!IsConnected) throw new InvalidOperationException("Not connected.");
            if (_eventLogClient is null) throw new InvalidOperationException("EventLog client not available.");
            if (_cts != null) return;

            Log?.Invoke("Starting monitoring...");

            _cts = new CancellationTokenSource();

            _lastEventId = await _eventLogClient.GetLastEntryIdAsync(_sessionToken);

            _loopTask = Task.Run(() => MonitorLoopAsync(_cts.Token));
        }

        public async Task StopMonitoringAsync()
        {
            if (_cts == null) return;

            Log?.Invoke("Stopping monitoring...");
            _cts.Cancel();

            try
            {
                if (_loopTask != null) await _loopTask;
            }
            catch (TaskCanceledException) { }
            finally
            {
                _cts.Dispose();
                _cts = null;
                _loopTask = null;
            }

            Log?.Invoke("Monitoring stopped.");
        }

        public async Task DisconnectAsync()
        {
            if (_sessionClient == null) return;

            Log?.Invoke("Disconnecting...");
            try
            {
                if (_sessionToken != Guid.Empty)
                    await _sessionClient.DisconnectAsync(_sessionToken);
            }
            catch (Exception ex)
            {
                Log?.Invoke("Disconnect error (ignored): " + ex.Message);
            }
            finally
            {
                _sessionToken = Guid.Empty;

                SafeAbort(_eventLogClient);
                SafeAbort(_cfgClient);
                SafeAbort(_sessionClient);

                _eventLogClient = null;
                _cfgClient = null;
                _sessionClient = null;

                _personCache.Clear();

                Log?.Invoke("Disconnected.");
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopMonitoringAsync();
            await DisconnectAsync();
        }

        private async Task MonitorLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var entries = await _eventLogClient!
                        .TakeEntriesStartingFromAsync(_lastEventId + 1, _sessionToken)
                        .ConfigureAwait(false);

                    foreach (var e in entries ?? Array.Empty<EventLogEntryData>())
                    {
                        if (e.ID > _lastEventId)
                            _lastEventId = e.ID;

                        // Filter: controller
                        if (e.ControllerID != MonitoredControllerId)
                            continue;

                        // Filter: access point id
                        var apId = GetAccessPointIdFromEvent(e);
                        if (apId is null) continue;

                        if (!AccessPointLabelById.TryGetValue(apId.Value, out var apLabel))
                            continue;

                        bool isDenied = AccessDeniedEventCodes.Contains(e.EventCode);
                        bool isRegular = !isDenied && (RegularEventCodes.Count == 0 || RegularEventCodes.Contains(e.EventCode));

                        if (!isDenied && !isRegular)
                            continue;

                        var personLine = await ResolvePersonLineAsync(e).ConfigureAwait(false);

                        if (isDenied)
                        {
                            var reason = DescribeActionStatus(e.ActionStatus);
                            EventReceived?.Invoke(new ApiEvent(
                                Kind: ApiEventKind.Denied,
                                Timestamp: e.LoggedOn,
                                Title: apLabel,
                                PersonLine: $"{personLine}",
                                DeniedReason: $"{reason}"
                            ));
                        }
                        else
                        {
                            EventReceived?.Invoke(new ApiEvent(
                                Kind: ApiEventKind.Regular,
                                Timestamp: e.LoggedOn,
                                Title: apLabel,
                                PersonLine: $"{personLine}",
                                DeniedReason: null
                            ));
                        }
                    }
                }
                catch (TaskCanceledException) { break; }
                catch (Exception ex)
                {
                    Log?.Invoke("MonitorLoop error: " + ex.Message);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                }
                catch (TaskCanceledException) { break; }
            }

            Log?.Invoke("Monitoring loop terminated.");
        }

        private async Task<string> ResolvePersonLineAsync(EventLogEntryData e)
        {
            if (_cfgClient == null) return "Nierozpoznano";

            if (e.PersonID is not int pid || pid <= 0)
                return "Nierozpoznano";

            if (_personCache.TryGetValue(pid, out var cached))
                return cached;

            try
            {
                var p = await _cfgClient.GetPersonByIdAsync(pid, _sessionToken);
                if (p != null)
                {
                    var line = BuildPersonLine(p);

                    if (string.IsNullOrWhiteSpace(line))
                        line = $"PersonID={pid}";

                    _personCache[pid] = line;
                    return line;
                }
            }
            catch
            {
                // ignore
            }

            return $"PersonID={pid}";
        }

        private static string BuildPersonLine(PersonData p)
        {
            string name = NormalizeWs(p.Name);
            string first = NormalizeWs(p.FirstName);
            string last = NormalizeWs(p.LastName);

            var firstLast = NormalizeWs($"{first} {last}");
            if (!string.IsNullOrWhiteSpace(name) &&
                !string.IsNullOrWhiteSpace(firstLast) &&
                string.Equals(name, firstLast, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }

            return NormalizeWs($"{name} {first} {last}");
        }

        private static string NormalizeWs(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            return string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }

        private static string DescribeActionStatus(int status) =>
            ActionStatusDescriptions.TryGetValue(status, out var desc)
                ? desc
                : $"ActionStatus={status}";

        private static int? GetAccessPointIdFromEvent(EventLogEntryData e)
        {
            int? st = GetEventInt(e, "SourceType");
            int? sid = GetEventInt(e, "SourceID");

            int? lt = GetEventInt(e, "LocationType");
            int? lid = GetEventInt(e, "LocationID");

            int? ot = GetEventInt(e, "OptionType");
            int? oid = GetEventInt(e, "OptionID");

            if (st == ObjectTypeAccessPoint && sid is > 0) return sid;
            if (lt == ObjectTypeAccessPoint && lid is > 0) return lid;
            if (ot == ObjectTypeAccessPoint && oid is > 0) return oid;

            return null;
        }

        private static int? GetEventInt(EventLogEntryData e, string propName)
        {
            var p = typeof(EventLogEntryData).GetProperty(propName);
            if (p == null) return null;
            var v = p.GetValue(e);
            if (v == null) return null;

            try { return Convert.ToInt32(v); }
            catch { return null; }
        }

        private static BasicHttpBinding CreateHttpBinding(TimeSpan send, TimeSpan receive)
        {
            return new BasicHttpBinding
            {
                MaxBufferSize = int.MaxValue,
                MaxReceivedMessageSize = int.MaxValue,
                ReaderQuotas = System.Xml.XmlDictionaryReaderQuotas.Max,
                AllowCookies = true,
                SendTimeout = send,
                ReceiveTimeout = receive,
                OpenTimeout = TimeSpan.FromSeconds(30),
                CloseTimeout = TimeSpan.FromSeconds(30),
            };
        }

        private static void SafeAbort(ICommunicationObject? obj)
        {
            if (obj == null) return;
            try
            {
                if (obj.State == CommunicationState.Faulted) obj.Abort();
                else obj.Close();
            }
            catch { obj.Abort(); }
        }
    }
}
