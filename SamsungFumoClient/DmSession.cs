﻿using System;
using System.Threading.Tasks;
using System.Xml;
using SamsungFumoClient.Exceptions;
using SamsungFumoClient.Network;
using SamsungFumoClient.Secure;
using SamsungFumoClient.SyncML;
using SamsungFumoClient.SyncML.Commands;
using SamsungFumoClient.SyncML.Elements;
using SamsungFumoClient.SyncML.Enum;
using SamsungFumoClient.Utils;

namespace SamsungFumoClient
{
    public class DmSession
    {
        private readonly OspHttpClient _client = new();

        private bool _isNotRegistered = true;
        private SyncDocument? _lastResponse;
        private byte[] _serverNonce = Array.Empty<byte>();

        public DmSession(Device device, bool register = true,
            string serverUrl = "https://dms.ospserver.net/v1/device/magicsync/mdm",
            string serverId = "x6g1q14r75", string serverPassword = "T1NQIERNIFNlcnZlcg==")
        {
            IsAborted = false;
            Device = device;
            ServerId = serverId;
            ServerPassword = serverPassword;
            ServerUrl = serverUrl;
            ClientPassword = string.Empty;
            ClientName = device.DeviceId;
            SessionId = DateTime.Now.Minute.ToString("X") + DateTime.Now.Second.ToString("X");

            _client.Device = device;
            _isNotRegistered = register;
            
            PostInit();
        }

        private async void PostInit()
        {
            ClientPassword = await CryptUtils.GenerateClientPassword(ClientName, ServerId) ?? string.Empty;
        }

        public string SessionId { get; }
        public int CurrentMessageId { private set; get; } = 1;
        public bool IsAborted { private set; get; }

        public Device Device { get; }
        public string ClientName { get; }
        public string ClientPassword { private set; get; }
        public string ServerId { get; }
        public string ServerPassword { get; }
        public string ServerUrl { private set; get; }

        public async Task<bool> SendFumoRegisterAsync()
        {
            if (_isNotRegistered)
            {
                _isNotRegistered = false;
                return await _client.SendFumoRegisterAsync(Device);
            }

            return true;
        }
        
        public async Task<SyncDocument> SendAsync(SyncBody body)
        {
            if (IsAborted)
            {
                throw new TransactionAbortedException();
            }

            if (_isNotRegistered)
            {
                _isNotRegistered = false;
                await _client.SendFumoRegisterAsync(Device);
            }

            var syncMlWriter = new SyncMlWriter();
            syncMlWriter.BeginDocument();
            syncMlWriter.WriteSyncHdr(await BuildHeader());
            syncMlWriter.WriteSyncBody(body);
            syncMlWriter.EndDocument();

            var responseBinary = await _client.SendWbxmlAsync(ServerUrl, syncMlWriter.GetBytes());
            var responseDocument = new SyncMlParser(responseBinary).Parse();
            ProcessServerResponse(responseDocument);
            _lastResponse = responseDocument;

            return responseDocument;
        }

        public async Task AbortSessionAsync()
        {
            if (IsAborted)
            {
                Log.W("DmSession.AbortSessionAsync: Session already aborted");
                return;
            }
            
            var syncMlWriter = new SyncMlWriter();
            syncMlWriter.BeginDocument();
            syncMlWriter.WriteSyncHdr(await BuildHeader());
            syncMlWriter.WriteSyncBody(new SyncBody()
            {
                Cmds = new[]
                {
                    new Alert()
                    {
                        CmdID = 1,
                        Data = AlertTypes.SESSION_ABORT
                    }
                }
            });
            syncMlWriter.EndDocument();
            try
            {
                await _client.SendWbxmlAsync(ServerUrl, syncMlWriter.GetBytes());
                Log.D("DmSession.AbortSessionAsync: Session abort has been sent");
            }
            catch (HttpException ex)
            {
                Log.E("DmSession.AbortSessionAsync: Failed due to HTTP error " + ex);
            }

            IsAborted = true;
        }

        public async Task<FirmwareObject?> RetrieveFirmwareObjectAsync(string descriptorUri)
        {
            var xml = await _client.GetDownloadDescriptorAsync(descriptorUri);
            if (xml == null)
            {
                return null;
            }
 
            var doc = new XmlDocument();
            doc.LoadXml(xml);

            XmlNode? mediaNode = null;
            foreach (XmlNode node in doc.ChildNodes)
            {
                if (node.Name == "media")
                {
                    mediaNode = node;
                }
            }
                
            if (mediaNode == null)
            {
                Log.E("DmSession.RetrieveFirmwareObject: Xml node /media not found in server response");
                return null;
            }

            var size = 0;
            string description = "No description included";
            string? uri = null;
            string? installParam = null;
            foreach (XmlNode child in mediaNode.ChildNodes)
            {
                switch (child.Name)
                {
                    case "description":
                        description = child.InnerText;
                        break;
                    case "size":
                        size = int.Parse(child.InnerText);
                        break;
                    case "objectURI":
                        uri = child.InnerText;
                        break;
                    case "installParam":
                        installParam = child.InnerText;
                        break;
                }
            }

            if (uri == null)
            {
                Log.E("DmSession.RetrieveFirmwareObject: ObjectUri is null");
                return null;
            }
            if (installParam == null)
            {
                Log.E("DmSession.RetrieveFirmwareObject: InstallParam is null");
                return null;
            }

            string? md5 = null;
            string? updateVersions = null;
            string? securityPatchVersion = null;
            foreach (var param in installParam.Split(';'))
            {
                var pair = param.Split('=');
                if (pair.Length != 2)
                {
                    Log.W($"DmSession.RetrieveFirmwareObject: Invalid InstallParam pair " +
                          $"(length is {pair.Length.ToString()} instead of 2)");
                    continue;
                }

                switch (pair[0])
                {
                    case "MD5":
                        md5 = string.IsNullOrWhiteSpace(pair[1]) ? null : pair[1];
                        break; 
                    case "updateFwV":
                        updateVersions = string.IsNullOrWhiteSpace(pair[1]) ? null : pair[1];
                        break;
                    case "securityPatchVersion":
                        securityPatchVersion = string.IsNullOrWhiteSpace(pair[1]) ? null : pair[1];
                        break;
                }
            }

            var versions = updateVersions?.Split('/', StringSplitOptions.TrimEntries);
            if (updateVersions == null || versions == null || versions.Length < 1)
            {
                Log.E("DmSession.RetrieveFirmwareObject: updateVersion is null");
                return null;
            }

            string apVersion = string.Empty;
            string? cpVersion = null;
            string? cscVersion = null;
            for (var index = 0; index < versions.Length; index++)
            {
                switch (index)
                {
                    case 0:
                        apVersion = versions[index];
                        continue; 
                    case 1:
                        cpVersion = versions[index];
                        continue; 
                    case 2:
                        cscVersion = versions[index];
                        continue;
                }
            }

            return new FirmwareObject()
            {
                Description = description,
                Size = size,
                Uri = uri,
                Md5 = md5,
                SecurityPatchVersion = securityPatchVersion,
                Version = new FirmwareVersion(
                    apVersion, 
                    cpVersion, 
                    cscVersion)
            };
        }

        public Cmd BuildAuthenticationStatus(int cmdId = 1)
        {
            return new Status
            {
                CmdID = cmdId,
                MsgRef = CurrentMessageId - 1,
                CmdRef = 0,
                Cmd = "SyncHdr",
                TargetRef = ClientName,
                SourceRef = ServerUrl.Contains('?')
                    ? ServerUrl.Substring(0, ServerUrl.LastIndexOf("?", StringComparison.Ordinal))
                    : ServerUrl,
                Data = "212"
            };
        }

        private void ProcessServerResponse(SyncDocument document)
        {
            // Find challenge section
            foreach (var cmd in document.SyncBody?.Cmds ?? Array.Empty<Cmd>())
            {
                if (cmd is Status status)
                {
                    if (status.Chal is {Meta: {NextNonce: { } nextNonce} meta})
                    {
                        if (meta.Type != "syncml:auth-md5" || meta.Format != "b64")
                        {
                            Log.W("DmSession: Challenge object uses an unsupported type or format");
                            continue;
                        }

                        _serverNonce = Base64.Decode(nextNonce);
                    }
                }
            }

            IsAborted = SyncMlUtils.HasServerAborted(document?.SyncBody?.Cmds);
            if (IsAborted)
            {
                Log.E("The server has aborted the session. No more messages must be sent.");
                return;
            }

            ServerUrl = document?.SyncHdr?.RespURI ?? ServerUrl;
            CurrentMessageId++;
        }

        private async Task<SyncHdr> BuildHeader()
        {
            Cred? cred = null;
            if (_lastResponse == null || !SyncMlUtils.IsAuthorizationAccepted(_lastResponse?.SyncBody?.Cmds))
            {
                cred = new Cred
                {
                    Meta = new Meta
                    {
                        Format = "b64",
                        Type = "syncml:auth-md5"
                    },
                    Data = await GenerateAuthDigest()
                };
            }

            return new SyncHdr()
            {
                SessionID = SessionId,
                MsgID = CurrentMessageId,
                Target = new Target
                {
                    LocURI = ServerUrl
                },
                Source = new Source
                {
                    LocURI = ClientName,
                    LocName = ClientName
                },
                Cred = cred,
                Meta = new Meta
                {
                    MaxMsgSize = 5120,
                    MaxObjSize = 1048576
                }
            };
        }

        private async Task<string?> GenerateAuthDigest()
        {
            var nextNonce = NextNonce();
            return await CryptUtils.MakeDigest(AuthTypes.Md5, ClientName, ClientPassword,
                nextNonce, null);
        }

        private byte[] NextNonce()
        {
            if (CurrentMessageId == 1 || _serverNonce.Length <= 0)
            {
                return Convert.FromBase64String(GenerateFactoryNonce());
            }

            return _serverNonce;
        }

        private static string GenerateFactoryNonce()
        {
            return Base64.Encode(RandomProvider.Random.Next() + "SSNextNonce");
        }
    }
}