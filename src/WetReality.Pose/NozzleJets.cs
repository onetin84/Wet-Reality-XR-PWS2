using MelonLoader;
using UnityEngine;

namespace WetReality;

// MEHRFACHSTRAHL UND TURBO - docs/PWS2-Notiz Turbo und Trident.md.
//
// Gemeldet: der zweite Strahl der Doppel-Turboduese (Urban X STREAM) reinigt
// nicht, und bei den Urban-X-Turboduesen drehen sich weder Aufsatz noch Strahl.
//
// ZWEI DEFEKTE, BEIDE VON DER MOD, BEIDE AUS DEM CODE BELEGT:
//
// 1. GameInput.RaycastUpdatePostfix (Bit 256) schrieb in JEDE
//    m_nozzleInstances[i] denselben Ursprung und dieselbe Richtung. Bei zwei
//    oder drei Strahlen fallen damit alle Raycasts auf den ersten; die VFX der
//    anderen haengen weiter unter ihrem eigenen RaySpawnPoint. Das ist genau
//    das Bild: sichtbarer zweiter Strahl, keine Wirkung.
//    KORREKTUR: hier wird je Instanz die Lage ihres RaySpawnPoint RELATIV zum
//    ersten gemessen, und der Postfix setzt sie auf die veroeffentlichte Pose
//    des ersten. Eine Instanz, deren Punkt der erste IST, bekommt Versatz null
//    und Drehung Identitaet - fuer jede Einzelduese also exakt das bisherige
//    Verhalten.
//
// 2. Bit 1 unterdrueckt SetWashDirection, und TrySetTurboNozzleWashDirection
//    ist dessen Aufgerufener (Zaehler 1:12837 / 4:1487, die 4 fror mit Bit 1
//    ein). NozzleInstance.TurboRotation las in allen Logs mit Turbo 0. Die
//    Drehung wird darum hier nachgebaut, wie in PWS1 1.35.0 im Headset
//    bestaetigt: Grundlage Euler(RaySpawnPointRotation), dann um die eigene
//    Vorwaertsachse kreisen, dann um TurboAngle kippen.
//    Die Zahlen kommen aus NozzleData.CleaningSettings (Klasse, Floats).
//    Seit 1.122.5 je Spitze aus m_secondaryNozzleCleaningSettings und mit
//    den Faktoren der Verlaengerung - siehe ReadTips. NICHT ueber
//    NozzleData.GetTipSettings(i): das gibt ein Struct BY VALUE ueber die
//    Interop-Grenze zurueck, die Form, die hier zweimal nativ abstuerzte.
//
// Strahl und Aufsatz drehen (Nutzer, 29.09.: "funktioniert alles"). Der
// Postfix schreibt TurboRotation mit; die Turbo-Haptik liest sie zurueck.
//
// Nur gelesen wird hier im LateUpdate (DriveRay), geschrieben wird nur auf die
// RaySpawnPoint-Knoten einer TURBO-Duese. Die 6DOF-Kette der Pistole bleibt
// unberuehrt.
internal sealed class NozzleJets
{
    private const int MaxJets = 8;

    private readonly float[] phase = new float[MaxJets];
    private IntPtr reportedNozzle;
    private int reportedCount = -1;
    private float nextFollowUp;
    private float nextLive;

    // JE SPITZE, NICHT JE DUESE - seit 1.122.5. Neu gelesen nur, wenn Duese
    // oder Verlaengerung wechseln; tipText traegt die Werte in den
    // Wechselblock.
    private readonly float[] tipAngle = new float[MaxJets];
    private readonly float[] tipSpeed = new float[MaxJets];
    private IntPtr tipNozzle;
    private IntPtr tipExtension = new(-1);
    private string tipText = "";

    // primaryOwned: Bit 128 ist gesetzt, die Mod fuehrt also die Drehung des
    // ersten RaySpawnPoint. Ohne das dreht der Turbo nicht - ueber einen
    // Knoten, den die Mod nicht fuehrt, wird hier nicht geschrieben.
    internal void Update(MelonLogger.Instance log, Il2CppFuturLab.PW2.WashEquipment? equipment,
        Transform primary, Transform? gun, bool perJet, bool turbo, bool primaryOwned,
        bool clampAnchors, bool liveReport, Il2CppFuturLab.PW2.ExtensionData? extension)
    {
        GameInput.JetCount = 0;
        GameInput.TurboActive = false;

        if (equipment is null || equipment == null)
            return;

        try
        {
            var instances = equipment.m_nozzleInstances;

            if (instances is null)
                return;

            var nozzle = equipment.m_nozzleData;
            var settings = nozzle is null || nozzle == null ? null : nozzle.CleaningSettings;
            var isTurbo = settings is not null && settings.IsTurbo;
            var turboAngle = settings is null ? 0f : settings.TurboAngle;
            var turboSpeed = settings is null ? 0f : settings.TurboSpeed;
            var extensionPointer = extension is null || extension == null ? IntPtr.Zero : extension.Pointer;
            var tipsFresh = isTurbo && nozzle is not null && nozzle != null
                && (nozzle.Pointer != tipNozzle || extensionPointer != tipExtension);

            if (tipsFresh)
                ReadTips(nozzle!, extension, turboAngle, turboSpeed);
            // Nur solange die Mod zielt (F2). Im flachen Modus laeuft
            // SetWashDirection samt Turbo des Spiels, und die Kegelmessung
            // unten soll dann dessen Werte sehen, nicht unsere.
            var spin = turbo && isTurbo && primaryOwned && GameInput.Active;
            // NUR DIE AKTIVEN. m_nozzleInstances hat im 1.122.0-Lauf immer drei
            // Eintraege, aktiv waren einer (Einzelturbo) oder zwei (Duo). Die
            // Reste zeigen auf alte Anker - beim U1-Turbo 29 cm seitlich und
            // 44 cm hinter der Muendung, Raycaster null - und 1.122.0 hat sie
            // mitgedreht.
            var active = equipment.m_activeNozzleCount;
            var count = Mathf.Clamp(Mathf.Min(instances.Length, active), 0, MaxJets);

            // TurboSpeed als Umdrehungen pro Sekunde, wie in PWS1 gemessen.
            // Ist die Einheit in PWS2 eine andere, zeigt es die Folgezeile.
            if (spin)
            {
                for (var i = 0; i < count; i++)
                    phase[i] = Mathf.Repeat(phase[i] + tipSpeed[i] * 360f * Time.deltaTime, 360f);
            }

            // ERST alle Drehungen schreiben, DANN relativ messen: sonst stammt
            // die Lage einer Aussenduese aus dem Zustand vor ihrem Kreisen.
            //
            // Grundlage ist Euler(RaySpawnPointRotation) fuer JEDE Duese, auch
            // die erste. Bei Einzelduesen ist das (0,0,0) gemessen, also die
            // Identitaet von Bit 128; bei der Doppelduese ist es der
            // Faecherwinkel, den Bit 128 der ersten sonst nimmt.
            if (spin)
            {
                for (var i = 0; i < count; i++)
                {
                    var instance = instances[i];
                    var spawn = SpawnOf(instance);

                    if (spawn is null)
                        continue;

                    spawn.localRotation = Quaternion.Euler(RotationOf(instance))
                        * Quaternion.AngleAxis(phase[i], Vector3.forward)
                        * Quaternion.Euler(tipAngle[i], 0f, 0f);
                }
            }

            // VOR dem Messen: die Lage relativ zum ersten soll schon die
            // korrigierte sein, sonst traegt der Raycast den Startversatz.
            if (liveReport && isTurbo && count >= 2)
                MeasureCone(log, instances, equipment.IsWashing, nozzle);

            if (clampAnchors)
                ClampSiblingAnchors(log, instances, count, primary);

            var inverse = Quaternion.Inverse(primary.rotation);
            var fresh = nozzle is not null && nozzle != null && nozzle.Pointer != reportedNozzle
                || count != reportedCount || tipsFresh;
            var lines = fresh ? new System.Text.StringBuilder() : null;

            // DIE LIVE-MESSUNG, einmal pro Sekunde, solange mehr als ein Strahl
            // oder ein Turbo aktiv ist. Der Block beim Wechsel entsteht im
            // ersten Frame, bevor das Spiel die Anker setzt: im 1.122.0-Lauf
            // stand der zweite Duo-Strahl dort 6 cm hinter dem ersten, im Bild
            // aber weit unten links. Wo er wirklich haengt, sagt nur diese Zeile.
            // Seit 1.122.3 hinter DevMode: sie hat den Startversatz des Duo
            // gefunden und kostet pro Sekunde einen Block. Wechselblock und
            // Klemmzeile bleiben, sie kommen nur bei Ereignissen.
            var live = liveReport && lines is null && (count > 1 || isTurbo)
                && Time.unscaledTime >= nextLive
                ? new System.Text.StringBuilder()
                : null;

            if (live is not null)
                nextLive = Time.unscaledTime + 1f;

            for (var i = 0; i < count; i++)
            {
                var instance = instances[i];
                var spawn = SpawnOf(instance);

                if (spawn is null || spawn.Pointer == primary.Pointer)
                {
                    GameInput.JetOffset[i] = Vector3.zero;
                    GameInput.JetRotation[i] = Quaternion.identity;
                }
                else
                {
                    GameInput.JetOffset[i] = primary.InverseTransformPoint(spawn.position);
                    GameInput.JetRotation[i] = inverse * spawn.rotation;
                }

                GameInput.TurboPhase[i] = phase[i];

                if (live is not null)
                    live.Append($"\n    jet[{i}] ").Append(Live(spawn, primary, gun));

                if (lines is null)
                    continue;

                var caster = instance.Raycaster;
                var offset = GameInput.JetOffset[i] * 100f;
                var turn = GameInput.JetRotation[i].eulerAngles;

                lines.Append($"\n    jet[{i}] ")
                    .Append(spawn is null
                        ? "no RaySpawnPoint"
                        : $"{spawn.parent?.parent?.name}/{spawn.parent?.name}"
                            + $"   active {spawn.gameObject.activeInHierarchy}"
                            + $"   primary {(spawn.Pointer == primary.Pointer ? "Y" : "n")}")
                    .Append($"   rel ({offset.x:0.0}, {offset.y:0.0}, {offset.z:0.0}) cm")
                    .Append($"   relEuler ({Signed(turn.x):0.0}, {Signed(turn.y):0.0}, {Signed(turn.z):0.0})")
                    .Append(caster is null
                        ? "   raycaster null"
                        : $"   spawnRotation {caster.RaySpawnPointRotation}"
                            + $"   anchorOffset {caster.NozzleAnchorOffset}");
            }

            GameInput.PerJet = perJet;
            GameInput.TurboActive = spin;
            GameInput.JetCount = count;

            if (live is not null)
                log.Msg($"nozzle jets live: {count} active   washing {(equipment.IsWashing ? "YES" : "no")}"
                    + $"   primary world {primary.position}" + live);

            if (lines is not null)
                ReportInventory(log);

            if (lines is not null)
            {
                reportedNozzle = nozzle is null || nozzle == null ? IntPtr.Zero : nozzle.Pointer;
                reportedCount = count;
                nextFollowUp = Time.unscaledTime + 2f;

                log.Msg($"nozzle jets: {(nozzle is null || nozzle == null ? "no NozzleData" : nozzle.name)}"
                    + $"   instances {count}   active {equipment.m_activeNozzleCount}"
                    + $"   turbo {(isTurbo ? "Y" : "n")} angle {turboAngle:0.###} speed {turboSpeed:0.###}"
                    + (isTurbo ? tipText : "")
                    + $"   spin {(spin ? "ON" : "off")} (pref {turbo}, bit 128 {primaryOwned})"
                    + $"   perJet {perJet}"
                    + lines);
            }
            else if (nextFollowUp > 0f && Time.unscaledTime >= nextFollowUp)
            {
                nextFollowUp = 0f;

                // Zurueckgelesen, nicht unser eigener Wert: TurboRotation aus
                // der Instanz, wie sie der Postfix hinterlassen hat, und die
                // Drehung des ersten Punkts. GEMESSEN: TurboRotation liegt
                // konstant ZWEI FRAMES hinter phase[0] (8 von 9 Zeilen bei
                // 90 fps, 1,88 bis 2,04 Frames) - unser eigener Wert mit
                // Verzug, kein fremder Schreiber.
                log.Msg($"nozzle jets: after 2 s   phase[0] {phase[0]:0.#} deg"
                    + $"   instance TurboRotation {instances[0].TurboRotation:0.#}"
                    + $"   spawnLocal {primary.localEulerAngles}"
                    + $"   jobWrites {GameInput.JobWrites}   perJetWrites {GameInput.PerJetWrites}");
            }
        }
        catch (Exception exception)
        {
            GameInput.JetCount = 0;
            GameInput.TurboActive = false;
            log.Warning($"nozzle jets threw {exception.GetType().Name}: {exception.Message}");
        }
    }

    // Elternkette bis zur Wurzel, Welt- und Lokalwerte, Lage relativ zum
    // ersten Punkt und ob der Knoten unter der Pistole haengt, die die Hand
    // fuehrt. Haengt er nicht darunter, folgt er der Hand nicht - das waere
    // der Versatz im Bild.
    private static string Live(Transform? spawn, Transform primary, Transform? gun)
    {
        if (spawn is null)
            return "no RaySpawnPoint";

        var chain = new System.Text.StringBuilder(spawn.name);
        var underGun = false;
        var depth = 0;

        for (var node = spawn.parent; node is not null && node != null; node = node.parent)
        {
            if (gun is not null && gun != null && node.Pointer == gun.Pointer)
                underGun = true;

            if (depth++ < 9)
                chain.Insert(0, node.name + "/");
        }

        var anchor = spawn.parent;
        var rel = primary.InverseTransformPoint(spawn.position) * 100f;
        var turn = (Quaternion.Inverse(primary.rotation) * spawn.rotation).eulerAngles;

        return $"underGun {(underGun ? "Y" : "n")}   world {spawn.position}"
            + $"   rel ({rel.x:0.0}, {rel.y:0.0}, {rel.z:0.0}) cm"
            + $"   relEuler ({Signed(turn.x):0.0}, {Signed(turn.y):0.0}, {Signed(turn.z):0.0})"
            + $"   spawnLocal {spawn.localPosition} {spawn.localEulerAngles}"
            + (anchor is null || anchor == null
                ? ""
                : $"   anchorLocal {anchor.localPosition} {anchor.localEulerAngles}")
            + $"   {chain}";
    }

    // DER STARTVERSATZ DER WEITEREN ANKER - Bit 65536 fuer alle, nicht nur
    // fuer den ersten.
    //
    // Gemessen im 1.122.2-Lauf, Doppelturbo direkt nach Spielstart: der erste
    // Anker las vor der Klemmung (-0.455, 0.361, 0.242), der zweite stand bei
    // (-0.45, 0.36, 0.24) - derselbe von PositionToFOV eingebackene Versatz,
    // ~40 cm neben dem ersten. Pose klemmt nur den ersten auf den Wert der
    // dritten Person (0, 0, 0.1), also blieb der zweite liegen. Nach einem
    // Duesenwechsel standen beide bei (0, 0, 0.10), Lage zueinander 0.
    //
    // DERSELBE BETRAG, NICHT DIESELBE POSITION. Pose meldet ueber NoteFudge,
    // was es am ersten Anker entfernt hat, und genau dieser Betrag geht EINMAL
    // von jedem weiteren Anker ab. Ein Entwurfsabstand zwischen den Ankern -
    // beim Trident nie gemessen - bleibt so unangetastet; beim Duo, wo beide
    // Anker gleich stehen, landet der zweite auf dem ersten.
    //
    // NUR GESCHWISTER DES ERSTEN, am selben Lokator. Anker unter einem anderen
    // Knoten - der Drehkopf des Flaechenreinigers haengt seine unter
    // SpinningArm - bleiben unberuehrt.
    //
    // Einmal je Anker, weil der Versatz einmal eingebacken wird und der Anker
    // danach still steht (gemessen: anchorLocal ueber alle Live-Zeilen
    // konstant). Jede neue Meldung fuer den ersten Anker - neuer Klon oder
    // neues Einbacken - raeumt die Liste und gilt fuer alle von vorn.
    private IntPtr fudgeAnchor;
    private Vector3 fudge;
    private readonly HashSet<IntPtr> fudgeRemoved = new();

    internal void NoteFudge(Transform anchor, Vector3 removed)
    {
        // Nur die ERSTE Meldung je Klon. Pose klemmt jeden Frame, in dem der
        // erste Anker abweicht; kaeme jede kleine Abweichung hier an, wuerde
        // derselbe Betrag wieder und wieder von den Geschwistern abgezogen.
        if (removed.sqrMagnitude < 1e-6f || anchor.Pointer == fudgeAnchor)
            return;

        fudgeAnchor = anchor.Pointer;
        fudge = removed;
        fudgeRemoved.Clear();
    }

    private void ClampSiblingAnchors(MelonLogger.Instance log,
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppFuturLab.PW2.WashEquipment.NozzleInstance> instances,
        int count, Transform primary)
    {
        var primaryAnchor = primary.parent;

        if (primaryAnchor is null || primaryAnchor == null || primaryAnchor.Pointer != fudgeAnchor)
            return;

        var locator = primaryAnchor.parent;

        if (locator is null || locator == null)
            return;

        for (var i = 0; i < count; i++)
        {
            var anchor = SpawnOf(instances[i])?.parent;

            if (anchor is null || anchor == null || anchor.Pointer == primaryAnchor.Pointer)
                continue;

            var parent = anchor.parent;

            if (parent is null || parent == null || parent.Pointer != locator.Pointer)
                continue;

            if (!fudgeRemoved.Add(anchor.Pointer))
                continue;

            var before = anchor.localPosition;
            anchor.localPosition = before - fudge;

            log.Msg($"nozzle jets: anchor[{i}] start offset removed like the first   "
                + $"{before} -> {anchor.localPosition}   ({fudge.magnitude * 100f:0.0} cm)");
        }
    }

    // DIE WERTE JE SPITZE UND DIE FAKTOREN DER VERLAENGERUNG.
    //
    // Spitze 0 aus CleaningSettings, jede weitere aus
    // m_secondaryNozzleCleaningSettings - ein Il2CppStructArray, dessen
    // Indexer den Speicher direkt liest (wie cornerBuffer in Pose), statt
    // GetTipSettings(i), das ein Struct BY VALUE zurueckgibt. Welche Spitze
    // "secondary" meint, ist NICHT gemessen: angewandt wird es nur, wenn die
    // Laenge genau NozzleCount - 1 ist. Sonst tragen alle Spitzen die Werte
    // der ersten, wie bis 1.122.4, und der Block sagt "layout unknown".
    //
    // Die Faktoren kommen aus ExtensionData.ExtensionWashModifiers,
    // GetTurbo*Multiplier(int) -> float, je Spitzenindex. Ohne Verlaengerung
    // oder bei einem Wurf bleibt der Faktor 1.
    private void ReadTips(Il2CppFuturLab.PW2.NozzleData nozzle,
        Il2CppFuturLab.PW2.ExtensionData? extension, float baseAngle, float baseSpeed)
    {
        tipNozzle = nozzle.Pointer;
        tipExtension = extension is null || extension == null ? IntPtr.Zero : extension.Pointer;

        for (var i = 0; i < MaxJets; i++)
        {
            tipAngle[i] = baseAngle;
            tipSpeed[i] = baseSpeed;
        }

        var text = new System.Text.StringBuilder();
        var tips = Mathf.Clamp(nozzle.NozzleCount, 1, MaxJets);

        try
        {
            var secondary = nozzle.m_secondaryNozzleCleaningSettings;
            var length = secondary is null ? 0 : secondary.Length;

            text.Append($"   tips {tips} secondary {length}");

            if (secondary is not null && length > 0 && length == tips - 1)
            {
                for (var i = 1; i < tips; i++)
                {
                    var tip = secondary[i - 1];
                    tipAngle[i] = tip.TurboAngle;
                    tipSpeed[i] = tip.TurboSpeed;
                }
            }
            else if (length > 0)
            {
                text.Append(" (layout unknown, first tip's values for all)");
            }
        }
        catch (Exception exception)
        {
            text.Append($"   secondary threw {exception.GetType().Name}");
        }

        try
        {
            var modifiers = extension is null || extension == null ? null : extension.ExtensionWashModifiers;

            if (modifiers is not null)
            {
                for (var i = 0; i < tips; i++)
                {
                    tipAngle[i] *= modifiers.GetTurboAngleMultiplier(i);
                    tipSpeed[i] *= modifiers.GetTurboSpeedMultiplier(i);
                }

                text.Append($"   extension {extension!.name}");
            }
            else
            {
                text.Append("   extension none");
            }
        }
        catch (Exception exception)
        {
            text.Append($"   multipliers threw {exception.GetType().Name}");
        }

        for (var i = 0; i < tips; i++)
            text.Append($"   tip[{i}] {tipAngle[i]:0.###} deg {tipSpeed[i]:0.###} rev/s");

        tipText = text.ToString();
    }

    // DER WINKEL ZWISCHEN DEN BEIDEN STRAHLEN, als Minimum und Maximum je
    // Sekunde - die Groesse, die nicht vom Zielen abhaengt. Kreisen zwei
    // Spitzen gegenlaeufig mit a0 und a1, laeuft er zwischen |a1-a0| und
    // a0+a1. Gemessen wird er im VR-Modus an UNSERER Drehung und im flachen
    // Modus (F2) an der des Spiels: der Vergleich sagt, ob die 4 Grad aus
    // m_secondaryNozzleCleaningSettings der Kippwinkel sind, den das Spiel
    // selbst verwendet. Gemeldet: der Duo streut seit 1.122.5 sichtbar
    // weiter als vorher.
    //
    // ERGEBNIS 29.09. 00:53: Spiel flach 2,93 bis 4,89 Grad, Mod in VR 3,00
    // bis 5,00 - die 4 Grad der zweiten Spitze sind die des Spiels. Seit
    // 1.122.9 nur noch mit DevMode.
    //
    // Nur beim Spruehen und hoechstens 10 Zeilen je Modus und Duesenwahl -
    // 1.122.6 hatte EINE Grenze fuer beide, und der VR-Teil verbrauchte sie.
    private float coneMin = float.MaxValue;
    private float coneMax;
    private int coneFrames;
    private float nextCone;
    private int coneLinesMod;
    private int coneLinesGame;
    private IntPtr coneNozzle;

    // DER FLACHE WEG: eigene WashEquipment-Suche, zweimal pro Sekunde, weil
    // WashProbe nur aus DriveRay heraus sucht. Schreibt nichts.
    private Il2CppFuturLab.PW2.WashEquipment? flatEquipment;
    private float nextFlatSearch;

    internal void MeasureFlat(MelonLogger.Instance log)
    {
        try
        {
            if ((flatEquipment is null || flatEquipment == null) && Time.unscaledTime >= nextFlatSearch)
            {
                nextFlatSearch = Time.unscaledTime + 0.5f;
                flatEquipment = UnityEngine.Object.FindObjectOfType<Il2CppFuturLab.PW2.WashEquipment>();
            }

            if (flatEquipment is null || flatEquipment == null)
                return;

            var nozzle = flatEquipment.m_nozzleData;
            var settings = nozzle is null || nozzle == null ? null : nozzle.CleaningSettings;
            var instances = flatEquipment.m_nozzleInstances;

            if (settings is null || !settings.IsTurbo || instances is null
                || Mathf.Min(instances.Length, flatEquipment.m_activeNozzleCount) < 2)
                return;

            MeasureCone(log, instances, flatEquipment.IsWashing, nozzle);
        }
        catch (Exception exception)
        {
            flatEquipment = null;
            log.Warning($"nozzle jets flat cone threw {exception.GetType().Name}: {exception.Message}");
        }
    }

    private void MeasureCone(MelonLogger.Instance log,
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppFuturLab.PW2.WashEquipment.NozzleInstance> instances,
        bool washing, Il2CppFuturLab.PW2.NozzleData? nozzle)
    {
        var pointer = nozzle is null || nozzle == null ? IntPtr.Zero : nozzle.Pointer;

        if (pointer != coneNozzle)
        {
            coneNozzle = pointer;
            coneLinesMod = 0;
            coneLinesGame = 0;
        }

        var first = SpawnOf(instances[0]);
        var second = SpawnOf(instances[1]);

        var used = GameInput.Active ? coneLinesMod : coneLinesGame;

        if (!washing || first is null || second is null || used >= 10)
        {
            coneFrames = 0;
            coneMin = float.MaxValue;
            coneMax = 0f;
            nextCone = Time.unscaledTime + 1f;
            return;
        }

        var angle = Vector3.Angle(first.forward, second.forward);
        coneMin = Mathf.Min(coneMin, angle);
        coneMax = Mathf.Max(coneMax, angle);
        coneFrames++;

        if (Time.unscaledTime < nextCone)
            return;

        if (GameInput.Active)
            coneLinesMod++;
        else
            coneLinesGame++;
        log.Msg($"nozzle jets cone: {(GameInput.Active ? "MOD (VR)" : "GAME (flat)")}"
            + $"   tip0-tip1 min {coneMin:0.00} max {coneMax:0.00} deg over {coneFrames} frames"
            + $"   mod would use {tipAngle[0]:0.##} / {tipAngle[1]:0.##} deg");

        coneFrames = 0;
        coneMin = float.MaxValue;
        coneMax = 0f;
        nextCone = Time.unscaledTime + 1f;
    }

    // DIE VIBRATIONSKATEGORIEN ALLER DUESEN, EINMAL - Offene Punkte
    // "Vibration nach NozzleData.VibrationCategory". Gemessen waren bisher nur
    // 40 = SprayingWhite und 15 = SprayingYellow; fuer Turbo, Trident,
    // Adaptable und die Flaechenreiniger-Koepfe fehlten die Werte. Statt jede
    // Duese einzeln anzuwaehlen, liest diese Liste alle geladenen NozzleData
    // beim ersten Duesenwechsel - und noch einmal, falls spaeter mehr geladen
    // sind (DLC). Nur Lesen, eine Zeile je Duese.
    private int inventoryCount = -1;

    private void ReportInventory(MelonLogger.Instance log)
    {
        try
        {
            var found = Resources.FindObjectsOfTypeAll(
                Il2CppInterop.Runtime.Il2CppType.Of<Il2CppFuturLab.PW2.NozzleData>());

            if (found.Length == 0 || found.Length <= inventoryCount)
                return;

            inventoryCount = found.Length;
            var text = new System.Text.StringBuilder(
                $"nozzle inventory: {found.Length} NozzleData loaded");

            for (var i = 0; i < found.Length; i++)
            {
                var nozzle = found[i]?.TryCast<Il2CppFuturLab.PW2.NozzleData>();

                if (nozzle is null || nozzle == null)
                    continue;

                var type = nozzle.NozzleType;
                var settings = nozzle.CleaningSettings;

                text.Append($"\n    {nozzle.name}")
                    .Append($"   short \"{(type is null || type == null ? "?" : type.ShortName)}\"")
                    .Append($"   group {(type is null || type == null ? -1 : type.NozzleGroup)}")
                    .Append($"   vib {nozzle.VibrationCategory}")
                    .Append($"   tips {nozzle.NozzleCount}")
                    .Append(settings is null
                        ? "   settings null"
                        : $"   turbo {(settings.IsTurbo ? "Y" : "n")}"
                            + $"   adaptable {(settings.IsAdaptable ? "Y" : "n")}"
                            + $"   head {(settings.IsHead ? "Y" : "n")}"
                            + $"   soap {(settings.IsSoapNozzle ? "Y" : "n")}");
            }

            log.Msg(text.ToString());
        }
        catch (Exception exception)
        {
            log.Warning($"nozzle inventory threw {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static float Signed(float angle) => angle > 180f ? angle - 360f : angle;

    private static Transform? SpawnOf(Il2CppFuturLab.PW2.WashEquipment.NozzleInstance instance)
    {
        var anchor = instance.NozzleAnchor;

        if (anchor is null || anchor == null)
            return null;

        var spawn = anchor.RaySpawnPoint;
        return spawn is null || spawn == null ? null : spawn;
    }

    private static Vector3 RotationOf(Il2CppFuturLab.PW2.WashEquipment.NozzleInstance instance)
    {
        var caster = instance.Raycaster;
        return caster is null ? Vector3.zero : caster.RaySpawnPointRotation;
    }
}
