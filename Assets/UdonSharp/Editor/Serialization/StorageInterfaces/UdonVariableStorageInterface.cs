﻿
using System;
using System.Collections.Generic;
using UdonSharp.Compiler;
using UnityEngine;
using VRC.Udon;
using VRC.Udon.Common;
using VRC.Udon.Common.Interfaces;

namespace UdonSharp.Serialization
{
    public class UdonVariableStorageInterface : IHeapStorage
    {
        class VariableValueStorage<T> : ValueStorage<T>
        {
            public string elementKey;
            public IUdonVariableTable table;

            public VariableValueStorage(string elementKey, IUdonVariableTable table)
            {
                this.elementKey = elementKey;
                this.table = table;
            }

            public override T Value
            {
                get
                {
                    return GetVariable<T>(table, elementKey); 
                }
                set
                {
                    SetVariable<T>(table, elementKey, value);
                }
            }
        }

        private static void SetVariable<T>(IUdonVariableTable table, string variableKey, T value)
        {
            System.Type type = typeof(T);

            bool isNull = false;
            if ((value is UnityEngine.Object unityEngineObject && unityEngineObject == null) || value == null)
                isNull = true;

            bool isRemoveType = (type == typeof(GameObject) ||
                type == typeof(Transform) ||
                type == typeof(UdonBehaviour));

            if (isNull && isRemoveType)
            {
                table.RemoveVariable(variableKey);
            }
            else
            {
                if (!table.TrySetVariableValue<T>(variableKey, value))
                {
                    UdonVariable<T> varVal = new UdonVariable<T>(variableKey, value);
                    if (!table.TryAddVariable(varVal))
                    {
                        Debug.LogError($"Could not write variable '{variableKey}' to public variables on UdonBehaviour");
                    }
                }
            }
        }

        private static T GetVariable<T>(IUdonVariableTable table, string variableKey)
        {
            T output;
            if (table.TryGetVariableValue<T>(variableKey, out output))
                return output;

            return default;
        }

        UdonBehaviour udonBehaviour;
        static Dictionary<UdonSharpProgramAsset, Dictionary<string, System.Type>> variableTypeLookup = new Dictionary<UdonSharpProgramAsset, Dictionary<string, Type>>();
        private System.Type GetElementType(string elementKey)
        {
            UdonSharpProgramAsset programAsset = (UdonSharpProgramAsset)udonBehaviour.programSource;

            Dictionary<string, System.Type> programTypeLookup;
            if (!variableTypeLookup.TryGetValue(programAsset, out programTypeLookup))
            {
                programTypeLookup = new Dictionary<string, Type>();
                foreach (FieldDefinition def in programAsset.fieldDefinitions.Values)
                {
                    if (def.fieldSymbol.declarationType.HasFlag(SymbolDeclTypeFlags.Public) || def.fieldSymbol.declarationType.HasFlag(SymbolDeclTypeFlags.Private))
                        programTypeLookup.Add(def.fieldSymbol.symbolOriginalName, def.fieldSymbol.symbolCsType);
                }
                variableTypeLookup.Add(programAsset, programTypeLookup);
            }

            System.Type fieldType;
            if (!programTypeLookup.TryGetValue(elementKey, out fieldType))
                return null;

            return fieldType;
        }

        public UdonVariableStorageInterface(UdonBehaviour udonBehaviour)
        {
            this.udonBehaviour = udonBehaviour;
        }

        public IValueStorage GetElementStorage(string elementKey)
        {
            System.Type elementType = GetElementType(elementKey);
            if (elementType == null)
                return null;

            return (IValueStorage)System.Activator.CreateInstance(typeof(VariableValueStorage<>).MakeGenericType(elementType), elementKey, udonBehaviour.publicVariables);
        }

        public object GetElementValueWeak(string elementKey)
        {
            object valueOut;
            udonBehaviour.publicVariables.TryGetVariableValue(elementKey, out valueOut);
            return valueOut;
        }

        public T GetElementValue<T>(string elementKey)
        {
            T variableVal;
            if (udonBehaviour.publicVariables.TryGetVariableValue<T>(elementKey, out variableVal))
                return variableVal;

            return default;
        }

        public void SetElementValueWeak(string elementKey, object value)
        {
            udonBehaviour.publicVariables.TrySetVariableValue(elementKey, value);
        }

        public void SetElementValue<T>(string elementKey, T value)
        {
            udonBehaviour.publicVariables.TrySetVariableValue<T>(elementKey, value);
        }
        
    }
}
