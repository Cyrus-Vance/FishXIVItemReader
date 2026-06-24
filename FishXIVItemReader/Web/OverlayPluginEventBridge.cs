using System;
using System.Linq.Expressions;
using System.Reflection;

namespace FishXIVItemReader.Web
{
    public sealed class OverlayPluginEventBridge : IDisposable
    {
        // OverlayPlugin 事件类型：分别对应心跳包和背包快照包。
        public const string PingEventType = "FishXIVItemReader.Ping";
        public const string InventorySnapshotEventType = "FishXIVItemReader.InventorySnapshot";

        private readonly object gate = new object();
        private object dispatcher;
        private Type jObjectType;
        private MethodInfo dispatchEventMethod;
        private MethodInfo parseJObjectMethod;
        private object latestSnapshotJObject;
        private string latestSnapshotEventJson;
        private DateTime lastInitializeAttemptUtc;
        private bool initialized;
        private bool disposed;

        public void TryConnect()
        {
            if (!disposed)
            {
                EnsureInitialized();
            }
        }

        public void PublishJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || disposed)
            {
                return;
            }

            var eventType = GetEventType(json);
            if (string.IsNullOrEmpty(eventType))
            {
                return;
            }

            var eventJson = CreateEventJson(eventType, json);
            if (string.IsNullOrEmpty(eventJson))
            {
                return;
            }

            if (string.Equals(eventType, InventorySnapshotEventType, StringComparison.Ordinal))
            {
                lock (gate)
                {
                    latestSnapshotEventJson = eventJson;
                }
            }

            if (!EnsureInitialized())
            {
                return;
            }

            try
            {
                var eventObject = ParseJObject(eventJson);
                if (eventObject == null)
                {
                    return;
                }

                if (string.Equals(eventType, InventorySnapshotEventType, StringComparison.Ordinal))
                {
                    lock (gate)
                    {
                        latestSnapshotJObject = eventObject;
                    }
                }

                dispatchEventMethod.Invoke(dispatcher, new[] { eventObject });
            }
            catch
            {
                initialized = false;
            }
        }

        public void Dispose()
        {
            disposed = true;
        }

        private bool EnsureInitialized()
        {
            if (initialized && dispatcher != null)
            {
                return true;
            }

            var now = DateTime.UtcNow;
            if ((now - lastInitializeAttemptUtc).TotalSeconds < 5)
            {
                return false;
            }

            lastInitializeAttemptUtc = now;

            try
            {
                var registryType = FindLoadedType("RainbowMage.OverlayPlugin.Registry");
                var dispatcherType = FindLoadedType("RainbowMage.OverlayPlugin.EventDispatcher");
                if (registryType == null || dispatcherType == null)
                {
                    return false;
                }

                var getContainerMethod = registryType.GetMethod(
                    "GetContainer",
                    BindingFlags.Public | BindingFlags.Static);
                if (getContainerMethod == null)
                {
                    return false;
                }

                var container = getContainerMethod.Invoke(null, null);
                if (container == null)
                {
                    return false;
                }

                var resolveMethod = container.GetType().GetMethod(
                    "Resolve",
                    new[] { typeof(Type) });
                if (resolveMethod == null)
                {
                    return false;
                }

                var resolvedDispatcher = resolveMethod.Invoke(container, new object[] { dispatcherType });
                if (resolvedDispatcher == null)
                {
                    return false;
                }

                var resolvedDispatchMethod = dispatcherType.GetMethod(
                    "DispatchEvent",
                    BindingFlags.Public | BindingFlags.Instance);
                if (resolvedDispatchMethod == null)
                {
                    return false;
                }

                var resolvedJObjectType = resolvedDispatchMethod.GetParameters()[0].ParameterType;
                var resolvedParseMethod = resolvedJObjectType.GetMethod(
                    "Parse",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(string) },
                    null);
                if (resolvedParseMethod == null)
                {
                    return false;
                }

                dispatcher = resolvedDispatcher;
                dispatchEventMethod = resolvedDispatchMethod;
                jObjectType = resolvedJObjectType;
                parseJObjectMethod = resolvedParseMethod;

                RegisterEventTypes(dispatcherType);
                RestoreLatestSnapshotCache();

                initialized = true;
                return true;
            }
            catch
            {
                initialized = false;
                dispatcher = null;
                return false;
            }
        }

        private void RegisterEventTypes(Type dispatcherType)
        {
            var simpleRegisterMethod = dispatcherType.GetMethod(
                "RegisterEventType",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(string) },
                null);

            if (simpleRegisterMethod == null)
            {
                return;
            }

            simpleRegisterMethod.Invoke(dispatcher, new object[] { PingEventType });

            var callbackRegisterMethod = FindCallbackRegisterEventTypeMethod(dispatcherType);
            if (callbackRegisterMethod == null)
            {
                simpleRegisterMethod.Invoke(dispatcher, new object[] { InventorySnapshotEventType });
                return;
            }

            var callbackType = callbackRegisterMethod.GetParameters()[1].ParameterType;
            var callback = CreateLatestSnapshotCallback(callbackType);
            callbackRegisterMethod.Invoke(dispatcher, new object[] { InventorySnapshotEventType, callback });
        }

        private MethodInfo FindCallbackRegisterEventTypeMethod(Type dispatcherType)
        {
            foreach (var method in dispatcherType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!string.Equals(method.Name, "RegisterEventType", StringComparison.Ordinal))
                {
                    continue;
                }

                var parameters = method.GetParameters();
                if (parameters.Length == 2 &&
                    parameters[0].ParameterType == typeof(string) &&
                    parameters[1].ParameterType.IsGenericType &&
                    parameters[1].ParameterType.GetGenericTypeDefinition() == typeof(Func<>) &&
                    parameters[1].ParameterType.GetGenericArguments()[0] == jObjectType)
                {
                    return method;
                }
            }

            return null;
        }

        private Delegate CreateLatestSnapshotCallback(Type callbackType)
        {
            var instance = Expression.Constant(this);
            var method = typeof(OverlayPluginEventBridge).GetMethod(
                "GetLatestSnapshotJObject",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var call = Expression.Call(instance, method);
            var cast = Expression.Convert(call, jObjectType);
            return Expression.Lambda(callbackType, cast).Compile();
        }

        private object GetLatestSnapshotJObject()
        {
            lock (gate)
            {
                return latestSnapshotJObject;
            }
        }

        private void RestoreLatestSnapshotCache()
        {
            string eventJson;
            lock (gate)
            {
                eventJson = latestSnapshotEventJson;
            }

            if (string.IsNullOrWhiteSpace(eventJson))
            {
                return;
            }

            var eventObject = ParseJObject(eventJson);
            lock (gate)
            {
                latestSnapshotJObject = eventObject;
            }
        }

        private object ParseJObject(string json)
        {
            return parseJObjectMethod.Invoke(null, new object[] { json });
        }

        private static Type FindLoadedType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type;
                try
                {
                    type = assembly.GetType(fullName, false);
                }
                catch
                {
                    continue;
                }

                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private static string GetEventType(string json)
        {
            var cmdType = TryReadCmdType(json);
            if (cmdType == 1)
            {
                return PingEventType;
            }

            if (cmdType == 2)
            {
                return InventorySnapshotEventType;
            }

            return null;
        }

        private static int TryReadCmdType(string json)
        {
            var markerIndex = json.IndexOf("\"cmdType\"", StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                return 0;
            }

            var colonIndex = json.IndexOf(':', markerIndex);
            if (colonIndex < 0)
            {
                return 0;
            }

            var index = colonIndex + 1;
            while (index < json.Length && char.IsWhiteSpace(json[index]))
            {
                index++;
            }

            var start = index;
            while (index < json.Length && char.IsDigit(json[index]))
            {
                index++;
            }

            int value;
            return start < index && int.TryParse(json.Substring(start, index - start), out value)
                ? value
                : 0;
        }

        private static string CreateEventJson(string eventType, string payloadJson)
        {
            var trimmed = payloadJson.Trim();
            if (trimmed.Length < 2 || trimmed[0] != '{')
            {
                return null;
            }

            return "{\"type\":\"" + eventType + "\"," + trimmed.Substring(1);
        }
    }
}
