// COPYRIGHT 2014 by the Open Rails project.
// 
// This file is part of Open Rails.
// 
// Open Rails is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// Open Rails is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with Open Rails.  If not, see <http://www.gnu.org/licenses/>.

using System;
using ORTS;
using ORTS.Scripting.Api;

namespace ORTS.Scripting.Script
{
    public class TCS_France : TrainControlSystem
    {
        enum CCS
        {
            KVB,
            TVM300,
            TVM430,
            ETCS
        }

        enum ETCSLevel
        {
            L0,
            NTC,
            L1,
            L2,
            L3
        }

        CCS ActiveCCS;
        ETCSLevel CurrentETCSLevel = ETCSLevel.L0;

        // Constants
        float GravityNpKg = 9.80665f;                       // g

        // Train parameters
        bool TVM300Present = true;                          // TVM300
        bool TVM430Present = false;                         // TVM430 (Not implemented)
        bool ETCSPresent = false;                           // ETCS (Not implemented)
        ETCSLevel ETCSMaxLevel = ETCSLevel.L0;              // ETCS maximum level (Not implemented)
        bool ElectroPneumaticBrake = true;                  // EP
        bool HeavyFreightTrain = false;                     // MA train, speed < 100 km/h
        float TrainLengthM = 400f;                          // L
        float MaxSpeedLimitMpS = MpS.FromKpH(320);          // VT
        float BrakingEstablishedDelayS = 2f;                // Tbo
        float DecelerationMpS2 = 0.9f;                      // Gamma

        // KVB speed control
        float KVBEmergencyBrakingAnticipationTimeS = 5f;    // Tx
        float KVBTrainSpeedLimitMpS = MpS.FromKpH(220);     // VT

        float KVBPreviousSignalDistanceM;                   // D
        float KVBCurrentSignalSpeedLimitMpS;
        float KVBNextSignalSpeedLimitMpS;
        float KVBSignalTargetSpeedMpS;
        float KVBSignalTargetDistanceM;
        float KVBDeclivity = 0f;                            // i

        float KVBCurrentSpeedPostSpeedLimitMpS;
        float KVBNextSpeedPostSpeedLimitMpS;
        float KVBSpeedPostTargetSpeedMpS;
        float KVBSpeedPostTargetDistanceM;
        float KVBAlertSpeedMpS;
        float KVBEBSpeedMpS;

        float KVBSignalEmergencySpeedCurveMpS;
        float KVBSignalAlertSpeedCurveMpS;
        float KVBSpeedPostEmergencySpeedCurveMpS;
        float KVBSpeedPostAlertSpeedCurveMpS;

        bool Overspeed = false;
        bool KVBEmergencyBraking = false;

        // TVM300 COVIT speed control
        float TVM300CurrentSpeedLimitMpS;
        float TVM300NextSpeedLimitMpS;
        float TVM300EmergencySpeedMpS;
        bool TVM300EmergencyBraking;

        // Vigilance monitoring (VACMA)
        bool VigilanceAlarm = false;
        bool VigilanceEmergency = false;

        public TCS_France() { }

        public override void Initialize()
        {
            if (!ElectroPneumaticBrake)
                BrakingEstablishedDelayS = 2f + 2f * (float)Math.Pow((double)TrainLengthM, 2D) * 0.00001f;
            else if (HeavyFreightTrain)
                BrakingEstablishedDelayS = 12f + TrainLenghtM / 200f;

            KVBPreviousSignalDistanceM = 0f;
            KVBCurrentSignalSpeedLimitMpS = KVBTrainSpeedLimitMpS;
            KVBNextSignalSpeedLimitMpS = KVBTrainSpeedLimitMpS;
            KVBSignalTargetSpeedMpS = KVBTrainSpeedLimitMpS;
            KVBSignalTargetDistanceM = 0f;

            KVBCurrentSpeedPostSpeedLimitMpS = KVBTrainSpeedLimitMpS;
            KVBNextSpeedPostSpeedLimitMpS = KVBTrainSpeedLimitMpS;
            KVBSpeedPostTargetSpeedMpS = KVBTrainSpeedLimitMpS;
            KVBSpeedPostTargetDistanceM = 0f;

            KVBAlertSpeedMpS = MpS.FromKpH(5);
            KVBEBSpeedMpS = MpS.FromKpH(10);

            Activated = true;
        }

        public override void Update()
        {
            SetNextSignalAspect(NextSignalAspect(0));

            if (CurrentPostSpeedLimitMpS() <= MpS.FromKpH(220f))
            {
                // Classic line = KVB active
                ActiveCCS = CCS.KVB;

                UpdateKVB();
                UpdateVACMA();

                if (KVBEmergencyBraking)
                {
                    if (SpeedMpS() >= 0.1f)
                        SetEmergency();
                    else
                    {
                        KVBEmergencyBraking = false;
                        SetPenaltyApplicationDisplay(false);
                    }
                }
            }
            else
            {
                // High speed line = TVM active

                // Activation control (KAr) in KVB system
                if (TVM300Present)
                {
                    ActiveCCS = CCS.TVM300;

                    UpdateTVM300();
                }
                else
                {
                    // TVM not activated because not present
                    ActiveCCS = CCS.KVB;

                    KVBEmergencyBraking = true;
                    SetPenaltyApplicationDisplay(true);
                    SetEmergency();
                }
            }
        }

        public override void SetEmergency()
        {
            SetPenaltyApplicationDisplay(true);
            if (IsBrakeEmergency())
                return;
            SetEmergencyBrake();

            SetThrottleController(0.0f); // Necessary while second locomotive isn't switched off during EB.
            SetPantographsDown();
        }

        protected void UpdateKVB()
        {
            // Decode signal aspect
            if (NextSignalDistanceM(0) > KVBPreviousSignalDistanceM)
            {
                switch (NextSignalAspect(0))
                {
                    case Aspect.Stop:
                        KVBNextSignalSpeedLimitMpS = MpS.FromKpH(10f);
                        KVBSignalTargetSpeedMpS = 0f;
                        KVBAlertSpeedMpS = MpS.FromKpH(2.5f);
                        KVBEBSpeedMpS = MpS.FromKpH(5f);
                        break;

                    case Aspect.StopAndProceed:
                        KVBNextSignalSpeedLimitMpS = MpS.FromKpH(30f);
                        KVBSignalTargetSpeedMpS = 0f;
                        KVBAlertSpeedMpS = MpS.FromKpH(5f);
                        KVBEBSpeedMpS = MpS.FromKpH(10f);
                        break;

                    case Aspect.Clear_1:
                    case Aspect.Clear_2:
                    case Aspect.Approach_1:
                    case Aspect.Approach_2:
                    case Aspect.Approach_3:
                    case Aspect.Restricted:
                        if (NextSignalSpeedLimitMpS(0) > 0f && NextSignalSpeedLimitMpS(0) < KVBTrainSpeedLimitMpS)
                            KVBNextSignalSpeedLimitMpS = NextSignalSpeedLimitMpS(0);
                        else
                            KVBNextSignalSpeedLimitMpS = KVBTrainSpeedLimitMpS;
                        KVBSignalTargetSpeedMpS = KVBNextSignalSpeedLimitMpS;
                        KVBAlertSpeedMpS = MpS.FromKpH(5f);
                        KVBEBSpeedMpS = MpS.FromKpH(10f);
                        break;
                }
            }
            KVBPreviousSignalDistanceM = NextSignalDistanceM(0);
            KVBSignalTargetDistanceM = NextSignalDistanceM(0);

            // Update current speed limit when speed is below the target or when the train approaches the signal
            if (NextSignalDistanceM(0) <= 10f)
                KVBCurrentSignalSpeedLimitMpS = KVBNextSignalSpeedLimitMpS;

            // Speed post speed limit preparation

            KVBNextSpeedPostSpeedLimitMpS = (NextPostSpeedLimitMpS(0) > 0 ? NextPostSpeedLimitMpS(0) : KVBTrainSpeedLimitMpS);
            KVBCurrentSpeedPostSpeedLimitMpS = CurrentPostSpeedLimitMpS();
            KVBSpeedPostTargetSpeedMpS = KVBNextSpeedPostSpeedLimitMpS;
            KVBSpeedPostTargetDistanceM = NextPostDistanceM(0);

            SetNextSpeedLimitMpS(Math.Min(KVBNextSignalSpeedLimitMpS, KVBNextSpeedPostSpeedLimitMpS));
            SetCurrentSpeedLimitMpS(Math.Min(KVBCurrentSignalSpeedLimitMpS, KVBCurrentSpeedPostSpeedLimitMpS));

            UpdateKVBSpeedCurve();
        }

        protected void UpdateKVBSpeedCurve()
        {
            Overspeed = false;

            KVBSignalEmergencySpeedCurveMpS =
                Math.Min( 
                    Math.Min(
                        Math.Max(
                            (float)Math.Sqrt(
                                Math.Pow((double)(BrakingEstablishedDelayS * DecelerationMpS2), 2D)
                                - 2D * Math.Pow((double)BrakingEstablishedDelayS, 2D) * (double)(DecelerationMpS2 * GravityNpKg * KVBDeclivity)
                                + Math.Pow((double)BrakingEstablishedDelayS * (double)(GravityNpKg * KVBDeclivity), 2D)
                                + 2D * (double)(KVBSignalTargetDistanceM * DecelerationMpS2)
                                - 2D * (double)(KVBSignalTargetDistanceM * GravityNpKg * KVBDeclivity)
                                + Math.Pow((double)KVBSignalTargetSpeedMpS, 2D)
                            )
                            - BrakingEstablishedDelayS * DecelerationMpS2
                            + BrakingEstablishedDelayS * GravityNpKg * KVBDeclivity,
                            KVBNextSignalSpeedLimitMpS + KVBEBSpeedMpS
                        ),
                        KVBTrainSpeedLimitMpS + MpS.FromKpH(10f)
                    ),
                    KVBCurrentSignalSpeedLimitMpS + KVBEBSpeedMpS
                );
            KVBSignalAlertSpeedCurveMpS =
                Math.Min(
                    Math.Min(
                        Math.Max(
                            (float)Math.Sqrt(
                                Math.Pow((double)(BrakingEstablishedDelayS * DecelerationMpS2), 2D)
                                - 2D * Math.Pow((double)BrakingEstablishedDelayS, 2D) * (double)(DecelerationMpS2 * GravityNpKg * KVBDeclivity)
                                + 2D * (double)(BrakingEstablishedDelayS * KVBEmergencyBrakingAnticipationTimeS) * Math.Pow((double)DecelerationMpS2, 2D)
                                - 4D * (double)(BrakingEstablishedDelayS * KVBEmergencyBrakingAnticipationTimeS * DecelerationMpS2 * GravityNpKg * KVBDeclivity)
                                + Math.Pow((double)(BrakingEstablishedDelayS * GravityNpKg * KVBDeclivity), 2D)
                                + 2D * (double)(BrakingEstablishedDelayS * KVBEmergencyBrakingAnticipationTimeS) * Math.Pow((double)(GravityNpKg * KVBDeclivity), 2D)
                                + Math.Pow((double)(KVBEmergencyBrakingAnticipationTimeS * DecelerationMpS2), 2D)
                                - 2D * Math.Pow((double)KVBEmergencyBrakingAnticipationTimeS, 2D) * (double)(DecelerationMpS2 * GravityNpKg * KVBDeclivity)
                                + Math.Pow((double)(KVBEmergencyBrakingAnticipationTimeS * GravityNpKg * KVBDeclivity), 2D)
                                + Math.Pow((double)KVBSignalTargetSpeedMpS, 2D)
                                + 2D * (double)(KVBSignalTargetDistanceM * DecelerationMpS2)
                                - 2D * (double)(KVBSignalTargetDistanceM * GravityNpKg * KVBDeclivity)
                            )
                            - BrakingEstablishedDelayS * DecelerationMpS2
                            + BrakingEstablishedDelayS * GravityNpKg * KVBDeclivity
                            - KVBEmergencyBrakingAnticipationTimeS * DecelerationMpS2
                            + KVBEmergencyBrakingAnticipationTimeS * GravityNpKg * KVBDeclivity,
                            KVBNextSignalSpeedLimitMpS + KVBAlertSpeedMpS
                        ),
                        KVBTrainSpeedLimitMpS + MpS.FromKpH(5f)
                    ),
                    KVBCurrentSignalSpeedLimitMpS + KVBAlertSpeedMpS
                );
            KVBSpeedPostEmergencySpeedCurveMpS =
                Math.Min(
                    Math.Min(
                        Math.Max(
                            (float)Math.Sqrt(
                                Math.Pow((double)(BrakingEstablishedDelayS * DecelerationMpS2), 2D)
                                - 2D * Math.Pow((double)BrakingEstablishedDelayS, 2D) * (double)(DecelerationMpS2 * GravityNpKg * KVBDeclivity)
                                + Math.Pow((double)BrakingEstablishedDelayS * (double)(GravityNpKg * KVBDeclivity), 2D)
                                + 2D * (double)(KVBSpeedPostTargetDistanceM * DecelerationMpS2)
                                - 2D * (double)(KVBSpeedPostTargetDistanceM * GravityNpKg * KVBDeclivity)
                                + Math.Pow((double)KVBSpeedPostTargetSpeedMpS, 2D)
                            )
                            - BrakingEstablishedDelayS * DecelerationMpS2
                            + BrakingEstablishedDelayS * GravityNpKg * KVBDeclivity,
                            KVBNextSpeedPostSpeedLimitMpS + MpS.FromKpH(10f)
                        ),
                        KVBTrainSpeedLimitMpS + MpS.FromKpH(10f)
                    ),
                    KVBCurrentSpeedPostSpeedLimitMpS + MpS.FromKpH(10f)
                );
            KVBSpeedPostAlertSpeedCurveMpS =
                Math.Min(
                    Math.Min(
                        Math.Max(
                            (float)Math.Sqrt(
                                Math.Pow((double)(BrakingEstablishedDelayS * DecelerationMpS2), 2D)
                                - 2D * Math.Pow((double)BrakingEstablishedDelayS, 2D) * (double)(DecelerationMpS2 * GravityNpKg * KVBDeclivity)
                                + 2D * (double)(BrakingEstablishedDelayS * KVBEmergencyBrakingAnticipationTimeS) * Math.Pow((double)DecelerationMpS2, 2D)
                                - 4D * (double)(BrakingEstablishedDelayS * KVBEmergencyBrakingAnticipationTimeS * DecelerationMpS2 * GravityNpKg * KVBDeclivity)
                                + Math.Pow((double)(BrakingEstablishedDelayS * GravityNpKg * KVBDeclivity), 2D)
                                + 2D * (double)(BrakingEstablishedDelayS * KVBEmergencyBrakingAnticipationTimeS) * Math.Pow((double)(GravityNpKg * KVBDeclivity), 2D)
                                + Math.Pow((double)(KVBEmergencyBrakingAnticipationTimeS * DecelerationMpS2), 2D)
                                - 2D * Math.Pow((double)KVBEmergencyBrakingAnticipationTimeS, 2D) * (double)(DecelerationMpS2 * GravityNpKg * KVBDeclivity)
                                + Math.Pow((double)(KVBEmergencyBrakingAnticipationTimeS * GravityNpKg * KVBDeclivity), 2D)
                                + Math.Pow((double)KVBSpeedPostTargetSpeedMpS, 2D)
                                + 2D * (double)(KVBSpeedPostTargetDistanceM * DecelerationMpS2)
                                - 2D * (double)(KVBSpeedPostTargetDistanceM * GravityNpKg * KVBDeclivity)
                            )
                            - BrakingEstablishedDelayS * DecelerationMpS2
                            + BrakingEstablishedDelayS * GravityNpKg * KVBDeclivity
                            - KVBEmergencyBrakingAnticipationTimeS * DecelerationMpS2
                            + KVBEmergencyBrakingAnticipationTimeS * GravityNpKg * KVBDeclivity,
                            KVBNextSpeedPostSpeedLimitMpS + MpS.FromKpH(5f)
                        ),
                        KVBTrainSpeedLimitMpS + MpS.FromKpH(5f)
                    ),
                    KVBCurrentSpeedPostSpeedLimitMpS + MpS.FromKpH(5f)
                );

            if (SpeedMpS() > KVBSignalAlertSpeedCurveMpS)
            {
                Overspeed = true;

                if (SpeedMpS() > KVBSignalEmergencySpeedCurveMpS)
                {
                    KVBEmergencyBraking = true;
                    SetPenaltyApplicationDisplay(true);
                    SetEmergency();
                }
            }

            if (SpeedMpS() > KVBSpeedPostAlertSpeedCurveMpS)
            {
                Overspeed = true;

                if (SpeedMpS() > KVBSpeedPostEmergencySpeedCurveMpS)
                {
                    KVBEmergencyBraking = true;
                    SetPenaltyApplicationDisplay(true);
                    SetEmergency();
                }
            }

            SetOverspeedWarningDisplay(Overspeed);
        }

        protected void UpdateTVM300()
        {
            TVM300NextSpeedLimitMpS = NextSignalSpeedLimitMpS(0);
            TVM300CurrentSpeedLimitMpS = CurrentSignalSpeedLimitMpS();
            if (TVM300CurrentSpeedLimitMpS <= MpS.FromKpH(80))
                TVM300EmergencySpeedMpS = MpS.FromKpH(5f);
            if (TVM300CurrentSpeedLimitMpS <= MpS.FromKpH(170))
                TVM300EmergencySpeedMpS = MpS.FromKpH(10f);
            else
                TVM300EmergencySpeedMpS = MpS.FromKpH(15f);

            SetNextSpeedLimitMpS(TVM300NextSpeedLimitMpS);
            SetCurrentSpeedLimitMpS(TVM300CurrentSpeedLimitMpS);

            if (TVM300EmergencyBraking || SpeedMpS() > TVM300CurrentSpeedLimitMpS + TVM300EmergencySpeedMpS) {
                TVM300EmergencyBraking = true;
                SetPenaltyApplicationDisplay(true);
                SetEmergency();
            }
            if (TVM300EmergencyBraking && SpeedMpS() <= TVM300CurrentSpeedLimitMpS)
            {
                TVM300EmergencyBraking = false;
                SetPenaltyApplicationDisplay(false);
            }
        }

        public override void AlerterReset()
        {
        }

        public override void AlerterPressed()
        {
            if (!Activated || VigilanceEmergency)
                return;
        }

        protected void UpdateVACMA()
        {
            if (!IsAlerterEnabled())
                return;
        }
    }
}