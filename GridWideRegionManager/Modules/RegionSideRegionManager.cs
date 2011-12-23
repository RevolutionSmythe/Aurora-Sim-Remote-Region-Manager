/* 
   Copyright 2011 Matthew Beardmore
   This file is part of Aurora-Sim Addon Modules.

   Aurora-Sim Addon Modules is free software:
   you can redistribute it and/or modify it under the
   terms of the GNU General Public License as published 
   by the Free Software Foundation, either version 3 
   of the License, or (at your option) any later version.
   Aurora-Sim Addon Modules is distributed in the hope that 
   it will be useful, but WITHOUT ANY WARRANTY; without 
   even the implied warranty of MERCHANTABILITY or FITNESS
   FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.
   You should have received a copy of the GNU General Public 
   License along with Aurora-Sim Addon Modules. If not, see http://www.gnu.org/licenses/.
*/
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Aurora.Framework;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;
using Aurora.Simulation.Base;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace Aurora.Addon.GridWideRegionManager
{
    public class RegionSideRegionManager : ISharedRegionStartupModule
    {
        private List<IScene> m_scenes = new List<IScene> ();
        private ISimulationBase m_simBase = null;
        public void Initialise (IScene scene, IConfigSource source, ISimulationBase openSimBase)
        {
            m_simBase = openSimBase;
        }

        public void PostInitialise (IScene scene, IConfigSource source, ISimulationBase openSimBase)
        {
        }

        public void FinishStartup (IScene scene, IConfigSource source, ISimulationBase openSimBase)
        {
        }

        public void PostFinishStartup (IScene scene, IConfigSource source, ISimulationBase openSimBase)
        {
            m_scenes.Add (scene);
        }

        public void Close (IScene scene)
        {
            //Tell the grid we are offline
            DoRegionOfflineCall (scene);
        }

        public void StartupComplete ()
        {
            foreach (IScene scene in m_scenes)
            {
                //Tell the grid we are all online
                DoRegionOnlineCall (scene);
            }
            IRegionInfoConnector rInfoConnector = Aurora.DataManager.DataManager.RequestPlugin<IRegionInfoConnector> ();
            if (rInfoConnector != null)
            {
                RegionInfo[] allRegions = rInfoConnector.GetRegionInfos (false);
                foreach (RegionInfo r in allRegions)
                {
                    DoRegionProvidedCall (m_scenes[0], r);
                }
            }
        }

        private void DoRegionOnlineCall (IScene scene)
        {
            IConfigurationService configService = scene.RequestModuleInterface<IConfigurationService> ();
            if (configService != null)
            {
                string url = configService.FindValueOf ("RegionManagerURL")[0];
                OSDMap data = new OSDMap ();
                data["Method"] = "RegionOnline";
                data["URL"] = BuildOurURL (scene, scene.RegionInfo);
                data["Region"] = new GridRegion (scene.RegionInfo).ToOSD ();
                WebUtils.PostToService (url, data, false, false);
            }
        }

        private void DoRegionProvidedCall (IScene someScene, RegionInfo rInfo)
        {
            IConfigurationService configService = someScene.RequestModuleInterface<IConfigurationService> ();
            if (configService != null)
            {
                string url = configService.FindValueOf ("RegionManagerURL")[0];
                OSDMap data = new OSDMap ();
                data["Method"] = "RegionProvided";
                data["URL"] = BuildOurURL (null, rInfo);
                data["Region"] = new GridRegion (rInfo).ToOSD ();
                WebUtils.PostToService (url, data, false, false);
            }
        }

        private void DoRegionOfflineCall (IScene scene)
        {
            IConfigurationService configService = scene.RequestModuleInterface<IConfigurationService> ();
            if (configService != null)
            {
                string url = configService.FindValueOf ("RegionManagerURL")[0];
                OSDMap data = new OSDMap ();
                data["Method"] = "RegionOffline";
                data["Region"] = new GridRegion (scene.RegionInfo).ToOSD ();
                WebUtils.PostToService (url, data, false, false);
            }
        }

        private string BuildOurURL (IScene ourScene, RegionInfo rInfo)
        {
            string url = "/rrm" + UUID.Random ();

            IHttpServer server = MainServer.Instance;
            server.AddStreamHandler (new RegionServerPostHandler (url, ourScene, rInfo, this));
            return server.HostName + ":" + server.Port + url;
        }

        public class RegionServerPostHandler : BaseStreamHandler
        {
            protected RegionSideRegionManager m_manager;
            protected IScene m_ourScene;
            protected RegionInfo m_ourRegInfo;

            public RegionServerPostHandler (string url, IScene scene, RegionInfo regInfo, RegionSideRegionManager manager) :
                base ("POST", url)
            {
                m_ourRegInfo = regInfo;
                m_ourScene = scene;
                m_manager = manager;
            }

            public override byte[] Handle (string path, Stream requestData,
                    OSHttpRequest httpRequest, OSHttpResponse httpResponse)
            {
                StreamReader sr = new StreamReader (requestData);
                string body = sr.ReadToEnd ();
                sr.Close ();
                body = body.Trim ();

                //m_log.DebugFormat("[XXX]: query String: {0}", body);

                OSDMap map = (OSDMap)OSDParser.DeserializeJson (body);
                GridRegion reg = new GridRegion ();
                switch (map["Method"].AsString ())
                {
                    case "Shutdown":
                        ShutdownType type = map["Type"] == "Immediate" ? ShutdownType.Immediate : ShutdownType.Delayed;
                        int seconds = map["Seconds"].AsInteger ();
                        m_manager.Shutdown (m_ourScene, type, seconds);
                        break;
                    case "Start":
                        m_manager.Start (m_ourRegInfo);
                        break;
                    case "StopScripts":
                        m_manager.StopScripts (m_ourScene);
                        break;
                    case "StartScripts":
                        m_manager.StartScripts(m_ourScene);
                        break;
                    case "LoadOAR":
                        m_manager.LoadOAR(m_ourScene, map["Data"].AsBinary(), map["Merge"],
                            map["SkipAssets"], map["OffsetX"], map["OffsetY"], map["OffsetZ"],
                            map["FlipX"], map["FlipY"], map["UseParcelOwnership"],
                            map["CheckOwnership"]);
                        break;
                    case "ChangeStartupStatus":
                        m_manager.ChangeStartupStatus (m_ourRegInfo, map["StatusEnabled"].AsBoolean());
                        break;
                    default:
                        break;
                }

                return new byte[0];
            }
        }

        internal void Shutdown (IScene scene, ShutdownType type, int seconds)
        {
            SceneManager manager = scene.RequestModuleInterface<SceneManager> ();
            if (type == ShutdownType.Delayed)
            {
                foreach (IScenePresence sp in scene.GetScenePresences ())
                {
                    if(!sp.IsChildAgent)
                        sp.ControllingClient.SendAlertMessage ("Region is shutting down in " + seconds + " seconds");
                }
            }
            manager.CloseRegion (scene, type, seconds);
        }

        internal void Start (RegionInfo rInfo)
        {
            SceneManager manager = m_simBase.ApplicationRegistry.RequestModuleInterface<SceneManager> ();
            manager.StartNewRegion (rInfo);
        }

        internal void ChangeStartupStatus (RegionInfo rInfo, bool enabled)
        {
            IRegionInfoConnector conn = Aurora.DataManager.DataManager.RequestPlugin<IRegionInfoConnector> ();
            rInfo.Disabled = !enabled;
            conn.UpdateRegionInfo (rInfo);
        }

        internal void StopScripts (IScene scene)
        {
            scene.RequestModuleInterface<IEstateModule> ().SetSceneCoreDebug (false, true, true);
        }

        internal void StartScripts (IScene scene)
        {
            scene.RequestModuleInterface<IEstateModule> ().SetSceneCoreDebug (true, true, true);
        }

        internal void LoadOAR(IScene scene, byte[] OARData, bool merge, bool skipAssets,
            int offsetX, int offsetY, int offsetZ, bool flipX, bool flipY, bool useParcelOwnership, bool checkOwnership)
        {
            MemoryStream stream = new MemoryStream(OARData);
            BufferedStream bufferedStream = new BufferedStream(stream, 1000000);
            scene.RequestModuleInterface<IRegionArchiverModule>().DearchiveRegion(bufferedStream, merge, skipAssets, offsetX, offsetY, offsetZ, flipX, flipY, useParcelOwnership, checkOwnership);
        }
    }
}
