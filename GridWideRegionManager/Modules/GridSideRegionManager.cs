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

namespace Aurora.Addon.GridWideRegionManager
{
    public class GridSideRegionManager : IService, IGridRegistrationUrlModule
    {
        private IRegistryCore m_registry;
        public void Initialize (IConfigSource config, IRegistryCore registry)
        {
            m_registry = registry;
            MainConsole.Instance.Commands.AddCommand ("close all regions", "close all regions", "Shuts down all regions in the grid", closeRegions);
            MainConsole.Instance.Commands.AddCommand ("close region", "close region", "Shuts down a region in the grid", closeRegion);
            MainConsole.Instance.Commands.AddCommand ("start region", "start region", "Starts up a region in the grid", startRegion);
            MainConsole.Instance.Commands.AddCommand ("change region startup status", "change region startup status", "Changes whether the given region will start up by default", changeRegionStartupStatus);
            MainConsole.Instance.Commands.AddCommand ("show regions", "show regions", "Shows information about regions in the grid", showRegions);
            MainConsole.Instance.Commands.AddCommand("stop region scripts", "stop region scripts", "Stops a region's scripts", stopScripts);
            MainConsole.Instance.Commands.AddCommand("start region scripts", "start region scripts", "Starts a region's scripts", startScripts);
            MainConsole.Instance.Commands.AddCommand("remote load oar", "remote load oar", "Loads an OAR on a remote region", remoteLoadOAR);
        }

        public void Start (IConfigSource config, IRegistryCore registry)
        {
            registry.RequestModuleInterface<IGridRegistrationService> ().RegisterModule (this);
        }

        public void FinishedStartup ()
        {
        }

        public string UrlName
        {
            get { return "RegionManagerURL"; }
        }

        public string GetUrlForRegisteringClient (string SessionID, uint port)
        {
            string url = "/grm" + UUID.Random ();

            IHttpServer server = m_registry.RequestModuleInterface<ISimulationBase> ().GetHttpServer (port);
            server.AddStreamHandler (new ServerPostHandler (url, this, m_registry));
            return url;
        }

        public void AddExistingUrlForClient (string SessionID, string url, uint port)
        {
            IHttpServer server = m_registry.RequestModuleInterface<ISimulationBase> ().GetHttpServer (port);
            server.AddStreamHandler (new ServerPostHandler (url, this, m_registry));
        }

        public void RemoveUrlForClient (string sessionID, string url, uint port)
        {
            IHttpServer server = m_registry.RequestModuleInterface<ISimulationBase> ().GetHttpServer (port);
            server.RemoveStreamHandler("POST", url);
        }

        public class ServerPostHandler : BaseStreamHandler
        {
            protected IRegistryCore m_registry;
            protected GridSideRegionManager m_manager;

            public ServerPostHandler (string url, GridSideRegionManager manager, IRegistryCore registry) :
                base ("POST", url)
            {
                m_manager = manager;
                m_registry = registry;
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
                switch (map["Method"].AsString())
                {
                    case "RegionOnline":
                        reg.FromOSD((OSDMap)map["Region"]);
                        m_manager.AddRegion (reg, map["URL"]);
                        break;
                    case "RegionProvided":
                        reg.FromOSD ((OSDMap)map["Region"]);
                        m_manager.AddAllRegion (reg, map["URL"]);
                        break;
                    case "RegionOffline":
                        reg.FromOSD ((OSDMap)map["Region"]);
                        m_manager.RemoveRegion (reg);
                        break;
                    default:
                        break;
                }

                return new byte[0];
            }
        }

        private Dictionary<GridRegion, string> m_regionsWeControl = new Dictionary<GridRegion, string> ();
        private Dictionary<GridRegion, string> m_allregionsWeControl = new Dictionary<GridRegion, string> ();
        internal void AddRegion (GridRegion reg, string callbackURL)
        {
            m_regionsWeControl[reg] = callbackURL;
        }

        internal void AddAllRegion (GridRegion reg, string callbackURL)
        {
            m_allregionsWeControl[reg] = callbackURL;
        }

        internal void RemoveRegion (GridRegion reg)
        {
            m_regionsWeControl.Remove(reg);
        }

        private void closeRegions (string[] cmd)
        {
            Dictionary<GridRegion, string> kvps = new Dictionary<GridRegion, string> (m_regionsWeControl);
            foreach (KeyValuePair<GridRegion, string> kvp in kvps)
            {
                OSDMap map = new OSDMap ();
                map["Method"] = "Shutdown";
                map["Type"] = "Immediate";
                map["Seconds"] = 0;
                WebUtils.PostToService (kvp.Value, map, false, false);
                MainConsole.Instance.Output ("Closed region " + kvp.Key.RegionName);
            }
            MainConsole.Instance.Output ("Closed all regions");
        }

        private void closeRegion (string[] cmd)
        {
            KeyValuePair<GridRegion, string> region = GetWhatRegion ("close");
            if (region.Key == null)
                return;
            OSDMap map = new OSDMap ();
            map["Method"] = "Shutdown";
            map["Type"] = MainConsole.Instance.CmdPrompt("Shutdown Type (Immediate or Delayed)", "Immediate") == "Immediate" ? "Immediate" : "Delayed";
            if(map["Type"] == "Delayed")
                map["Seconds"] = int.Parse(MainConsole.Instance.CmdPrompt("Seconds before delayed shutdown", "60"));
            WebUtils.PostToService (region.Value, map, false, false);
            MainConsole.Instance.Output ("Closed region " + region.Key.RegionName);
        }

        private void startRegion(string[] cmd)
        {
            KeyValuePair<GridRegion, string> region = GetWhatRegion("start");
            if (region.Key == null)
                return;
            OSDMap map = new OSDMap();
            map["Method"] = "Start";
            WebUtils.PostToService(region.Value, map, false, false);
            MainConsole.Instance.Output("Started region " + region.Key.RegionName);
        }

        private void remoteLoadOAR(string[] cmd)
        {
            KeyValuePair<GridRegion, string> region = GetWhatRegion("load an oar on");
            if (region.Key == null)
                return;
            OSDMap map = new OSDMap();
            map["Method"] = "LoadOAR";
            map["Data"] = File.ReadAllBytes(MainConsole.Instance.CmdPrompt("OAR File to load: "));
            string parameters = MainConsole.Instance.CmdPrompt("Any parameters (as used with a normal load OAR command): ");
            bool mergeOar = false;
            bool skipAssets = false;
            int offsetX = 0;
            int offsetY = 0;
            int offsetZ = 0;
            bool flipX = false;
            bool flipY = false;
            bool useParcelOwnership = false;
            bool checkOwnership = false;

            int i = 0;
            List<string> newParams = new List<string>(parameters.Split(' '));
            foreach (string param in parameters.Split(' '))
            {
                if (param.StartsWith("--skip-assets", StringComparison.CurrentCultureIgnoreCase))
                {
                    skipAssets = true;
                    newParams.Remove(param);
                }
                if (param.StartsWith("--merge", StringComparison.CurrentCultureIgnoreCase))
                {
                    mergeOar = true;
                    newParams.Remove(param);
                }
                if (param.StartsWith("--OffsetX", StringComparison.CurrentCultureIgnoreCase))
                {
                    string retVal = param.Remove(0, 10);
                    int.TryParse(retVal, out offsetX);
                    newParams.Remove(param);
                }
                if (param.StartsWith("--OffsetY", StringComparison.CurrentCultureIgnoreCase))
                {
                    string retVal = param.Remove(0, 10);
                    int.TryParse(retVal, out offsetY);
                    newParams.Remove(param);
                }
                if (param.StartsWith("--OffsetZ", StringComparison.CurrentCultureIgnoreCase))
                {
                    string retVal = param.Remove(0, 10);
                    int.TryParse(retVal, out offsetZ);
                    newParams.Remove(param);
                }
                if (param.StartsWith("--FlipX", StringComparison.CurrentCultureIgnoreCase))
                {
                    flipX = true;
                    newParams.Remove(param);
                }
                if (param.StartsWith("--FlipY", StringComparison.CurrentCultureIgnoreCase))
                {
                    flipY = true;
                    newParams.Remove(param);
                }
                if (param.StartsWith("--UseParcelOwnership", StringComparison.CurrentCultureIgnoreCase))
                {
                    useParcelOwnership = true;
                    newParams.Remove(param);
                }
                if (param.StartsWith("--CheckOwnership", StringComparison.CurrentCultureIgnoreCase))
                {
                    checkOwnership = true;
                    newParams.Remove(param);
                }
                i++;
            }
            map["Merge"] = mergeOar;
            map["SkipAssets"] = skipAssets;
            map["OffsetX"] = offsetX;
            map["OffsetY"] = offsetY;
            map["OffsetZ"] = offsetZ;
            map["FlipX"] = flipX;
            map["FlipY"] = flipY;
            map["UseParcelOwnership"] = useParcelOwnership;
            map["CheckOwnership"] = checkOwnership;
            WebUtils.PostToService(region.Value, map, int.MaxValue, false, false);
            MainConsole.Instance.Output("Loading OAR on region " + region.Key.RegionName);
        }

        private KeyValuePair<GridRegion, string> GetWhatRegion (string action)
        {
            string cmd = MainConsole.Instance.CmdPrompt ("What region should we " + action + " (region name)", "");
            if (cmd == "")
                return new KeyValuePair<GridRegion, string> ();
            foreach (KeyValuePair<GridRegion, string> kvp in m_regionsWeControl)
            {
                if (kvp.Key.RegionName.ToLower ().Contains (cmd.ToLower ()))
                    return kvp;
            }
            foreach (KeyValuePair<GridRegion, string> kvp in m_allregionsWeControl)
            {
                if (kvp.Key.RegionName.ToLower ().Contains (cmd.ToLower ()))
                    return kvp;
            }
            return new KeyValuePair<GridRegion, string> ();
        }

        private void showRegions (string[] cmd)
        {
            bool allPossible = MainConsole.Instance.CmdPrompt("Show all possible regions?", "no") != "no";
            foreach (KeyValuePair<GridRegion, string> kvp in m_regionsWeControl)
            {
                MainConsole.Instance.Output (kvp.Key.RegionName + " - Online");
            }
            if (allPossible)
            {
                foreach (KeyValuePair<GridRegion, string> kvp in m_allregionsWeControl)
                {
                    if(!m_regionsWeControl.ContainsKey(kvp.Key))
                        MainConsole.Instance.Output (kvp.Key.RegionName + " - Offline");
                }
            }
        }

        private void changeRegionStartupStatus (string[] cmd)
        {
            KeyValuePair<GridRegion, string> region = GetWhatRegion ("change");
            if (region.Key == null)
                return;
            OSDMap map = new OSDMap ();
            map["Method"] = "ChangeStartupStatus";
            map["StatusEnabled"] = MainConsole.Instance.CmdPrompt ("Start the region on the next instance restart?", "yes") == "yes";
            WebUtils.PostToService (region.Value, map, false, false);
        }

        private void startScripts (string[] cmd)
        {
            KeyValuePair<GridRegion, string> region = GetWhatRegion ("change");
            if (region.Key == null)
                return;
            OSDMap map = new OSDMap ();
            map["Method"] = "StartScripts";
            WebUtils.PostToService (region.Value, map, false, false);
        }

        private void stopScripts (string[] cmd)
        {
            KeyValuePair<GridRegion, string> region = GetWhatRegion ("change");
            if (region.Key == null)
                return;
            OSDMap map = new OSDMap ();
            map["Method"] = "StopScripts";
            WebUtils.PostToService (region.Value, map, false, false);
        }
    }
}
