using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

#pragma warning disable CS1069 // The type name 'MonoBehaviour' could not be found. It is forwarded to assembly 'UnityEngine.CoreModule'

namespace KRASH_VisualsBridge
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class KRASH_VisualsBridge : MonoBehaviour
    {
        private const string TAG = "[KRASH_VisualsBridge]";

        private bool lastSimState = false;
        private bool applied = false;

        // états sauvegardés pour restauration à la fin de la simu
        private bool scattererWireframeBefore = false;
        private bool parallaxWasActive = true;
        private bool eveWasActive = true;
        private bool dynamicCloudWasActive = true;

        void Start()
        {
            Debug.Log(TAG + " loaded");
        }

        void Update()
        {
            bool simActive = GetKrashSimActive();

            if (simActive && !lastSimState) OnSimStart();
            else if (!simActive && lastSimState) OnSimEnd();

            lastSimState = simActive;
        }

        // ---------- Détection de l'état KRASH ----------
        bool GetKrashSimActive()
        {
            try
            {
                var krashAsm = AssemblyLoader.loadedAssemblies
                    .FirstOrDefault(a => a.name.Equals("KRASH", StringComparison.OrdinalIgnoreCase));
                if (krashAsm == null) return false;

                var shelterType = krashAsm.assembly.GetType("KRASH.KRASHShelter");
                var persistentField = shelterType?.GetField("persistent", BindingFlags.Public | BindingFlags.Static);
                var persistentObj = persistentField?.GetValue(null);
                
                // FIX: Added null check for persistentObj before accessing its type
                if (persistentObj == null) return false;
                
                var activeField = persistentObj.GetType().GetField("shelterSimulationActive", BindingFlags.Public | BindingFlags.Instance);
                return activeField != null && (bool)activeField.GetValue(persistentObj);
            }
            catch (Exception e)
            {
                Debug.LogWarning(TAG + " GetKrashSimActive: " + e.Message);
                return false;
            }
        }

        // ---------- Transitions ----------
        void OnSimStart()
        {
            Debug.Log(TAG + " Début de simulation -> application des overrides visuels");
            scattererWireframeBefore = SetScattererWireframe(true);
            parallaxWasActive       = SetGameObjectsActive("Parallax", false);
            eveWasActive             = SetGameObjectsActive("EVE", false);       // ajuste si le nom réel diffère
            dynamicCloudWasActive    = SetGameObjectsActive("Cloud", false);      // ajuste si le nom réel diffère
            applied = true;
        }

        void OnSimEnd()
        {
            if (!applied) return;
            Debug.Log(TAG + " Fin de simulation -> restauration");
            SetScattererWireframe(scattererWireframeBefore);
            SetGameObjectsActive("Parallax", parallaxWasActive);
            SetGameObjectsActive("EVE", eveWasActive);
            SetGameObjectsActive("Cloud", dynamicCloudWasActive);
            applied = false;
        }

        // ---------- Scatterer : bascule du mode wireframe par réflexion ----------
        bool SetScattererWireframe(bool state)
        {
            try
            {
                var scattererAsm = AssemblyLoader.loadedAssemblies
                    .FirstOrDefault(a => a.name.IndexOf("scatterer", StringComparison.OrdinalIgnoreCase) >= 0);
                if (scattererAsm == null)
                {
                    Debug.Log(TAG + " Scatterer non trouvé");
                    return false;
                }

                foreach (var type in scattererAsm.assembly.GetTypes())
                {
                    // FIX: Improved null checking and type safety for Instance member lookup
                    var propInfo = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                    var fieldInfo = type.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                    
                    MemberInfo instanceMember = propInfo != null ? propInfo : (MemberInfo)fieldInfo;
                    if (instanceMember == null) continue;

                    object instance = instanceMember is PropertyInfo pi ? pi.GetValue(null, null)
                                                                        : ((FieldInfo)instanceMember).GetValue(null);
                    if (instance == null) continue;

                    var wireframeMember = type.GetMembers(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name.IndexOf("wireframe", StringComparison.OrdinalIgnoreCase) >= 0
                                          && (m is FieldInfo f && f.FieldType == typeof(bool)
                                          || m is PropertyInfo p && p.PropertyType == typeof(bool)));
                    if (wireframeMember == null) continue;

                    bool previous;
                    if (wireframeMember is FieldInfo fld)
                    {
                        previous = (bool)fld.GetValue(instance);
                        fld.SetValue(instance, state);
                    }
                    else
                    {
                        var prop = (PropertyInfo)wireframeMember;
                        previous = (bool)prop.GetValue(instance, null);
                        prop.SetValue(instance, state, null);
                    }

                    Debug.Log(TAG + $" Scatterer.{type.Name}.{wireframeMember.Name} -> {state} (était {previous})");
                    return previous;
                }

                Debug.LogWarning(TAG + " Aucun membre 'wireframe' trouvé dans Scatterer — voir la liste ci-dessous pour ajuster :");
                DumpBoolMembers(scattererAsm.assembly);
            }
            catch (Exception e)
            {
                Debug.LogWarning(TAG + " SetScattererWireframe: " + e);
            }
            return false;
        }

        // ---------- Parallax / EVE / DynamicCloud : bascule par nom de GameObject ----------
        bool SetGameObjectsActive(string nameContains, bool active)
        {
            bool foundAny = false;
            bool previousState = active;  // FIX: Default to target state instead of true
            var matchedObjects = new List<GameObject>();  // FIX: Track all matched objects
            var all = UnityEngine.Object.FindObjectsOfType<GameObject>();
            
            foreach (var go in all)
            {
                if (go.transform.parent != null) continue; // racines seulement
                if (go.name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0) continue;

                previousState = go.activeSelf;  // FIX: Capture state before change
                go.SetActive(active);
                matchedObjects.Add(go);
                foundAny = true;
                Debug.Log(TAG + $" GameObject '{go.name}' -> active={active}");
            }
            
            if (!foundAny)
                Debug.LogWarning(TAG + $" Aucun GameObject racine contenant '{nameContains}' trouvé");
            
            return previousState;
        }

        // ---------- Utilitaire de debug ----------
        void DumpBoolMembers(Assembly asm)
        {
            foreach (var type in asm.GetTypes())
            {
                foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
                    if (f.FieldType == typeof(bool))
                        Debug.Log(TAG + $"   candidat champ: {type.FullName}.{f.Name}");
                foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
                    if (p.PropertyType == typeof(bool))
                        Debug.Log(TAG + $"   candidat propriété: {type.FullName}.{p.Name}");
            }
        }
    }
}

#pragma warning restore CS1069
