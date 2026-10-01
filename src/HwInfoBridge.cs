using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace SystemPulse
{
    // Lecture (seule) de la mémoire partagée de HWiNFO64, quand il tourne à côté : un format documenté par son
    // éditeur pour ce cas exact (Rainmeter, MSI Afterburner On-Screen Display font pareil). On ne fait que LIRE ;
    // rien n'est installé ni embarqué — HWiNFO reste un programme séparé, à installer et lancer soi-même.
    //
    // Pourquoi : les zones ACPI (RefreshCpuTemp dans Metrics.cs) sont gratuites et sans droits, mais beaucoup de
    // cartes mères de bureau n'exposent rien. LibreHardwareMonitorLib (lecture directe, universelle) a été testée
    // et écartée : son pilote est bloqué par Windows par défaut depuis 2022-2023 (liste des pilotes vulnérables),
    // admin ou non. HWiNFO fait ce travail lui-même avec son propre pilote (non bloqué, testé) ; on lit juste son
    // résultat. Limite vérifiée sur ce PC : la mémoire partagée de HWiNFO (même gratuit) n'est lisible que si
    // System Pulse tourne AUSSI en administrateur (même bouton « Relancer en administrateur » que pour les FPS) :
    // HWiNFO protège sa mémoire partagée par une liste de contrôle d'accès qui exige le même niveau de droits.
    // Dans sa version gratuite, HWiNFO limite aussi ce partage à 12 h après son propre démarrage.
    //
    // Format (non documenté officiellement en détail, mais stable et utilisé par de nombreux outils tiers ;
    // vérifié ici contre une vraie installation HWiNFO 8.54) : "Global\HWiNFO_SENS_SM2", en-tête à décalages
    // explicites (pour rester compatible si HWiNFO ajoute des champs), tableau de "capteurs" (regroupements,
    // ex. "CPU [#0] : Intel Core i5-7600K : DTS") et tableau de "lectures" (une valeur, avec son capteur parent).
    internal sealed class HwInfoBridge : IDisposable
    {
        private const string MapName = @"Global\HWiNFO_SENS_SM2";
        private const string MapNameLocal = "HWiNFO_SENS_SM2"; // repli si "Global\" est refusé (rare)
        private const uint Signature = 0x53695748; // "HWiS" en mémoire (ordre des octets Windows)
        private const uint ReadingTypeTemperature = 1;

        private MemoryMappedFile map;
        private MemoryMappedViewAccessor view;

        private static string ReadFixedString(MemoryMappedViewAccessor v, long offset, int length)
        {
            byte[] buffer = new byte[length];
            v.ReadArray(offset, buffer, 0, length);
            int nul = Array.IndexOf(buffer, (byte)0);
            if (nul < 0) nul = buffer.Length;
            return Encoding.ASCII.GetString(buffer, 0, nul);
        }

        private bool EnsureOpen()
        {
            if (view != null) return true;
            try { map = MemoryMappedFile.OpenExisting(MapName); }
            catch (Exception)
            {
                try { map = MemoryMappedFile.OpenExisting(MapNameLocal); }
                catch (Exception) { return false; } // HWiNFO non lancé, partage désactivé, ou droits insuffisants
            }
            try { view = map.CreateViewAccessor(); return true; }
            catch (Exception) { Close(); return false; }
        }

        private void Close()
        {
            if (view != null) { try { view.Dispose(); } catch (Exception) { } view = null; }
            if (map != null) { try { map.Dispose(); } catch (Exception) { } map = null; }
        }

        public double? TryReadCpuTemperature()
        {
            try { return ReadCpuTemperature(); }
            catch (Exception) { Close(); return null; } // la mémoire a pu disparaître pendant la lecture
        }

        private double? ReadCpuTemperature()
        {
            if (!EnsureOpen()) return null;
            MemoryMappedViewAccessor v = view;

            if (v.ReadUInt32(0) != Signature) { Close(); return null; }
            uint sensorOffset = v.ReadUInt32(0x14), sensorSize = v.ReadUInt32(0x18), sensorCount = v.ReadUInt32(0x1C);
            uint entryOffset = v.ReadUInt32(0x20), entrySize = v.ReadUInt32(0x24), entryCount = v.ReadUInt32(0x28);
            // Garde-fous : une mémoire corrompue ou un format trop différent ne doit jamais faire planter l'appli.
            if (sensorSize < 16 || sensorSize > 4096 || sensorCount > 4096) { Close(); return null; }
            if (entrySize < 64 || entrySize > 4096 || entryCount > 20000) { Close(); return null; }

            // Regroupements ("CPU [#0] : <nom>", carte mère, GPU...) : sert à préférer la sonde CPU de base (DTS)
            // à ses doublons (carte mère, variante "Enhanced") quand plusieurs portent le même intitulé.
            Dictionary<uint, string> sensorNames = new Dictionary<uint, string>();
            for (uint i = 0; i < sensorCount; i++)
            {
                long o = sensorOffset + (long)i * sensorSize;
                sensorNames[i] = ReadFixedString(v, o + 8, 128);
            }

            string bestPackageLabel = null; double bestPackageValue = 0;
            string anyPackageLabel = null; double anyPackageValue = 0;
            double? amdLabel = null;
            double cpuGroupMax = double.NegativeInfinity; bool hasCpuGroupMax = false;

            for (uint i = 0; i < entryCount; i++)
            {
                long o = entryOffset + (long)i * entrySize;
                if (v.ReadUInt32(o) != ReadingTypeTemperature) continue;
                uint sensorIndex = v.ReadUInt32(o + 4);
                string group; sensorNames.TryGetValue(sensorIndex, out group);
                if (group == null || group.IndexOf("CPU", StringComparison.OrdinalIgnoreCase) < 0) continue; // carte mère, GPU... hors sujet
                string label = ReadFixedString(v, o + 0x0C, 128);
                double value = v.ReadDouble(o + 0x011C);
                if (value < -40 || value > 130) continue; // capteur aberrant : ignoré plutôt qu'affiché

                if (string.Equals(label, "CPU Package", StringComparison.OrdinalIgnoreCase))
                {
                    anyPackageLabel = label; anyPackageValue = value;
                    // Sonde de base (nom de groupe finissant par "DTS", le capteur numérique d'origine Intel/AMD)
                    // préférée à ses doublons (carte mère, variante "Enhanced") qui portent le même intitulé.
                    if (bestPackageLabel == null || group.EndsWith("DTS", StringComparison.OrdinalIgnoreCase))
                    { bestPackageLabel = label; bestPackageValue = value; }
                }
                else if (label.IndexOf("Tctl", StringComparison.OrdinalIgnoreCase) >= 0 || label.IndexOf("Tdie", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    amdLabel = value; // processeurs AMD : pas de "CPU Package", "Tctl"/"Tdie" fait référence
                }
                if (!hasCpuGroupMax || value > cpuGroupMax) { cpuGroupMax = value; hasCpuGroupMax = true; }
            }

            if (bestPackageLabel != null) return bestPackageValue;
            if (amdLabel.HasValue) return amdLabel.Value;
            if (anyPackageLabel != null) return anyPackageValue;
            if (hasCpuGroupMax) return cpuGroupMax; // repli : la plus chaude des sondes CPU, sans étiquette reconnue
            return null;
        }

        public void Dispose() { Close(); }
    }
}
