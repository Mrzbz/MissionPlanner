﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using log4net;
using MissionPlanner.Controls;
using MissionPlanner.HIL;
using MissionPlanner.Utilities;

namespace MissionPlanner.Swarm.SRB
{
    public class DroneGroup
    {
        public enum Mode
        {
            idle,
            takeoff,
            alongside,
            z,
            LandAlt,
            Land
        }

        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        Mode _currentMode = Mode.idle;

        public Mode CurrentMode
        {
            get { return _currentMode; }
            set
            {
                _currentMode = value;
                Console.WriteLine("Mode Change " + value.ToString());
            }
        }

        public List<Drone> Drones = new List<Drone>();

        public void UpdatePositions()
        {
            // get current positions and velocitys
            foreach (var drone in Drones)
            {
                if (drone.Location == null)
                    drone.Location = new PointLatLngAlt();
                drone.Location.Lat = drone.MavState.cs.lat;
                drone.Location.Lng = drone.MavState.cs.lng;
                drone.Location.Alt = drone.MavState.cs.alt;
                if (drone.Velocity == null)
                    drone.Velocity = new Vector3();
                drone.Velocity.x = drone.MavState.cs.vx;
                drone.Velocity.y = drone.MavState.cs.vy;
                drone.Velocity.z = drone.MavState.cs.vz;
            }

            switch (CurrentMode)
            {
                case Mode.idle:
                    // request positon at 10hz
                    foreach (var drone in Drones)
                    {
                        var MAV = drone.MavState;

                        MAV.parent.requestDatastream(MAVLink.MAV_DATA_STREAM.POSITION, 10, MAV.sysid, MAV.compid);
                        MAV.cs.rateposition = 10;

                        drone.takeoffdone = false;
                    }
                    CurrentMode = Mode.takeoff;
                    break;
                case Mode.takeoff:
                    int g = -1;
                    foreach (var drone in Drones)
                    {
                        g++;
                        var MAV = drone.MavState;
                        try
                        {
                            // guided mode
                            if (!MAV.cs.mode.ToLower().Equals("guided"))
                                MAV.parent.setMode(MAV.sysid, MAV.compid, "GUIDED");
                            // arm
                            if (!MAV.cs.armed)
                                if (!MAV.parent.doARM(MAV.sysid, MAV.compid, true))
                                    return;
                        }
                        catch (Exception ex)
                        {
                            log.Error(ex);
                            Loading.ShowLoading("Communication with one of the drones is failing\n" + ex);

                            return;
                        }
                        // set drone target position
                        drone.TargetLocation = drone.Location;
                        drone.TargetLocation.Alt = TakeOffAlt + g * 2;

                        try
                        {
                            // takeoff
                            if (MAV.cs.alt < drone.TargetLocation.Alt - 0.5 && !drone.takeoffdone)
                                if (MAV.parent.doCommand(MAV.sysid, MAV.compid, MAVLink.MAV_CMD.TAKEOFF, 0, 0, 0, 0, 0,
                                    0, (float)drone.TargetLocation.Alt))
                                    drone.takeoffdone = true;
                        }
                        catch (Exception ex)
                        {
                            log.Error(ex);
                            Loading.ShowLoading("Communication with one of the drones is failing\n" + ex);

                            return;
                        }

                        drone.MavState.GuidedMode.x = 0;
                        drone.MavState.GuidedMode.y = 0;
                        drone.MavState.GuidedMode.z = (float) drone.TargetLocation.Alt;

                        // wait for takeoff
                        if (MAV.cs.alt < drone.TargetLocation.Alt - 0.5)
                        {
                            Thread.Sleep(100);
                            // check we are still armed
                            if (!MAV.cs.armed)
                                return;

                            // move on to next drone
                            continue;
                        }

                        // we should only get here once takeoff alt has been archived by this drone.

                        // position control
                        drone.SendPositionVelocity(drone.TargetLocation, Vector3.Zero);

                        drone.MavState.GuidedMode.x = (float) drone.TargetLocation.Lat;
                        drone.MavState.GuidedMode.y = (float) drone.TargetLocation.Lng;
                        drone.MavState.GuidedMode.z = (float) drone.TargetLocation.Alt;
                    }
                    // wait for all to get within takeoff alt
                    foreach (var drone in Drones)
                    {
                        var MAV = drone.MavState;

                        // wait for takeoff
                        if (MAV.cs.alt < drone.TargetLocation.Alt - 0.5)
                        {
                            Thread.Sleep(100);
                            // check we are still armed
                            if (!MAV.cs.armed)
                                return;

                            // reloop to force takeoff 
                            return;
                        }
                    }
                    CurrentMode = Mode.alongside;
                    break;
                case Mode.alongside:
                    int a = 0;
                    foreach (var drone in Drones)
                    {
                        // set drone target position
                        drone.TargetLocation = GetDronePosition(drone, a);
                        a++;

                        // position control
                        drone.SendPositionVelocity(drone.TargetLocation, drone.TargetVelocity);

                        drone.MavState.GuidedMode.x = (float) drone.TargetLocation.Lat;
                        drone.MavState.GuidedMode.y = (float) drone.TargetLocation.Lng;
                        drone.MavState.GuidedMode.z = (float) drone.TargetLocation.Alt;
                    }

                    break;
                case Mode.z:
                    int d = 0;
                    foreach (var drone in Drones)
                    {
                        // set drone target position
                        drone.TargetLocation = GetDronePosition(drone, d, MaxOffset / 2);
                        d++;

                        // position control
                        drone.SendPositionVelocity(drone.TargetLocation, drone.TargetVelocity);

                        drone.MavState.GuidedMode.x = (float)drone.TargetLocation.Lat;
                        drone.MavState.GuidedMode.y = (float)drone.TargetLocation.Lng;
                        drone.MavState.GuidedMode.z = (float)drone.TargetLocation.Alt;
                    }
                    break;
                case Mode.LandAlt:
                    var e = 0;
                    foreach (var drone in Drones)
                    {
                        drone.TargetLocation = GetDronePosition(drone, e);
                        drone.TargetLocation.Alt = TakeOffAlt + e;

                        // position control
                        drone.SendPositionVelocity(drone.TargetLocation, Vector3.Zero);

                        drone.MavState.GuidedMode.z = (float) drone.TargetLocation.Alt;

                        Thread.Sleep(200);

                        drone.SendPositionVelocity(drone.TargetLocation, Vector3.Zero);

                        e++;
                    }
                    // check status
                    foreach (var drone in Drones)
                    {
                        // wait for alt hit
                        while (Math.Abs(drone.MavState.cs.alt - drone.TargetLocation.Alt) > 0.5)
                        {
                            if (!drone.MavState.cs.armed)
                                break;
                            Thread.Sleep(200);
                            drone.SendPositionVelocity(drone.TargetLocation, Vector3.Zero);
                        }

                        Thread.Sleep(200);
                    }
                    CurrentMode = Mode.Land;
                    break;
                case Mode.Land:
                    Drone landing = null;
                    foreach (var drone in Drones)
                    {
                        if (drone.MavState.cs.armed)
                        {
                            landing = drone;
                            var basePosition = GetBasePosition();
                            var basevelocity = GetBaseVelocity();

                            drone.SendYaw(basePosition.Heading);

                            drone.TargetLocation = basePosition;
                            drone.TargetVelocity = basevelocity;

                            if (drone.Location.GetDistance(drone.TargetLocation) < 1)
                            {
                                drone.TargetLocation.Alt = 0;
                            }
                            else
                            {
                                drone.TargetLocation.Alt = TakeOffAlt;
                            }

                            drone.SendPositionVelocity(drone.TargetLocation, Vector3.Zero);

                            // one drone at a time
                            break;
                        }
                    }

                    int f = 0;
                    foreach (var drone in Drones)
                    {
                        if(drone == landing)
                            continue;

                        // set drone target position
                        drone.TargetLocation = GetDronePosition(drone, f);
                        f++;

                        // position control
                        drone.SendPositionVelocity(drone.TargetLocation, drone.TargetVelocity);

                        drone.MavState.GuidedMode.x = (float)drone.TargetLocation.Lat;
                        drone.MavState.GuidedMode.y = (float)drone.TargetLocation.Lng;
                        drone.MavState.GuidedMode.z = (float)drone.TargetLocation.Alt;
                    }
                    break;
            }
        }

        private Dictionary<Drone, double> zprogress = new Dictionary<Drone, double>();

        private PointLatLngAlt GetDronePosition(Drone drone, int a, double distance = 0)
        {
            var basepos = GetBasePosition();

            double sin = 0;

            if (zprogress.ContainsKey(drone))
            {
                sin = Math.Sin(zprogress[drone]);
                zprogress[drone] += 0.1;
            }
            else
            {
                zprogress[drone] = 0;
            }

            if (a == 0)
                return basepos.newpos(basepos.Heading + 90, a * MinOffset + distance + (sin/2.0 * (MaxOffset - MinOffset)));
            if (a == 1)
                return basepos.newpos(basepos.Heading - 90, a * MinOffset + distance + (sin/2.0 * (MaxOffset - MinOffset)));

            return drone.Location;
        }

        private adsb.PointLatLngAltHdg GetBasePosition()
        {
            return new adsb.PointLatLngAltHdg(SerialInjectGPS.ubxpvt.lat / 1e7, SerialInjectGPS.ubxpvt.lon / 1e7,
                SerialInjectGPS.ubxpvt.height, SerialInjectGPS.ubxpvt.headVeh, "", DateTime.Now);
        }

        private Vector3 GetBaseVelocity()
        {
            return new Vector3(SerialInjectGPS.ubxvelned.velN, SerialInjectGPS.ubxvelned.velE, SerialInjectGPS.ubxvelned.velD);
        }

        public float TakeOffAlt { get; set; }
        public float MinOffset { get; set; }
        public float MaxOffset { get; set; }
    }
}