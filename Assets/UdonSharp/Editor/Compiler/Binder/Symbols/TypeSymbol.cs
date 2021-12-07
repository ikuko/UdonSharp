﻿
using System;
using Microsoft.CodeAnalysis;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using UdonSharp.Compiler.Binder;
using UdonSharp.Compiler.Udon;

namespace UdonSharp.Compiler.Symbols
{
    internal abstract class TypeSymbol : Symbol
    {
        private readonly object _dictionaryLazyInitLock = new object();
        private ConcurrentDictionary<ISymbol, Symbol> _typeSymbols;

        public new ITypeSymbol RoslynSymbol => (ITypeSymbol)base.RoslynSymbol;

        public bool IsValueType => RoslynSymbol.IsValueType;

        public bool IsArray => RoslynSymbol.TypeKind == TypeKind.Array;

        public bool IsEnum => RoslynSymbol.TypeKind == TypeKind.Enum;

        public bool IsUdonSharpBehaviour => !IsArray && ((INamedTypeSymbol) RoslynSymbol).IsUdonSharpBehaviour();

        public ExternTypeSymbol UdonType { get; protected set; }

        private TypeSymbol _elementType;
        public TypeSymbol ElementType
        {
            get
            {
                if (!IsArray)
                    throw new InvalidOperationException("Cannot get element type on non-array types");

                return _elementType;
            }
            protected set => _elementType = value;
        }
        
        public TypeSymbol BaseType { get; }
        public ImmutableArray<TypeSymbol> TypeArguments { get; }

        protected TypeSymbol(ISymbol sourceSymbol, AbstractPhaseContext context)
            : base(sourceSymbol, context)
        {
            // ReSharper disable once VirtualMemberCallInConstructor
            if (RoslynSymbol.BaseType != null && !IsExtern) // We don't use the base type on extern types and if we bind the base here, it can cause loops due to how Udon maps types
                BaseType = context.GetTypeSymbol(RoslynSymbol.BaseType);
            
            if (IsArray)
                ElementType = context.GetTypeSymbol(((IArrayTypeSymbol)sourceSymbol).ElementType);

            if (sourceSymbol is INamedTypeSymbol sourceNamedType)
            {
                TypeArguments = sourceNamedType.TypeArguments.Length > 0
                    ? sourceNamedType.TypeArguments.Select(context.GetTypeSymbol).ToImmutableArray()
                    : ImmutableArray<TypeSymbol>.Empty;

                if (RoslynSymbol.OriginalDefinition != RoslynSymbol)
                    OriginalSymbol = context.GetSymbol(RoslynSymbol.OriginalDefinition);
                else
                    OriginalSymbol = this;
            }
            else
            {
                TypeArguments = ImmutableArray<TypeSymbol>.Empty;
            }
        }

        private void InitSymbolDict()
        {
            if (_typeSymbols != null)
                return;

            lock (_dictionaryLazyInitLock)
            {
                if (_typeSymbols != null)
                    return;

                _typeSymbols = new ConcurrentDictionary<ISymbol, Symbol>();
            }
        }

        private bool _bound;

        public override bool IsBound => _bound;

        public override void Bind(BindContext context)
        {
            if (_bound)
                return;

            if (IsArray)
            {
                _bound = true;
                return;
            }

            if (TypeArguments.Length > 0 && this == OriginalSymbol)
            {
                _bound = true;
                return;
            }
            
            context.CurrentNode = RoslynSymbol.DeclaringSyntaxReferences.First().GetSyntax();

            if (IsUdonSharpBehaviour)
            {
                if (RoslynSymbol.AllInterfaces.Length > 1) // Be lazy and ignore the serialization callback receiver since this is temporary
                    throw new NotImplementedException("Interfaces are not yet handled by U#");
                
                SetupAttributes(context);
            }

            var members = RoslynSymbol.GetMembers();

            foreach (var member in members.Where(member => (!member.IsImplicitlyDeclared || member.Kind == SymbolKind.Field)))
            {
                switch (member)
                {
                    case IFieldSymbol _:
                    case IPropertySymbol property when !property.IsStatic && IsUdonSharpBehaviour:
                    case IMethodSymbol method when !method.IsStatic && IsUdonSharpBehaviour:
                        Symbol boundSymbol = context.GetSymbol(member);
                        
                        if (!boundSymbol.IsBound)
                            using (context.OpenMemberBindScope(boundSymbol))
                                boundSymbol.Bind(context);
                        
                        break;
                }
            }

            _bound = true;
        }

        public Dictionary<TypeSymbol, HashSet<Symbol>> CollectReferencedUnboundSymbols(BindContext context, IEnumerable<Symbol> extraBindMembers)
        {
            Dictionary<TypeSymbol, HashSet<Symbol>> referencedTypes = new Dictionary<TypeSymbol, HashSet<Symbol>>();

            IEnumerable<Symbol> allMembers = GetMembers(context).Concat(extraBindMembers);

            foreach (Symbol member in allMembers)
            {
                if (member.DirectDependencies == null)
                    continue;

                foreach (Symbol dependency in member.DirectDependencies.Where(e => !e.IsBound))
                {
                    if (dependency is TypeSymbol typeSymbol)
                    {
                        if (!referencedTypes.ContainsKey(typeSymbol))
                            referencedTypes.Add(typeSymbol, new HashSet<Symbol>());
                    }
                    else
                    {
                        TypeSymbol containingType = dependency.ContainingType;
                        if (!referencedTypes.ContainsKey(containingType))
                            referencedTypes.Add(containingType, new HashSet<Symbol>());

                        referencedTypes[containingType].Add(dependency);
                    }
                }
            }

            if (BaseType != null && !BaseType.IsBound && 
                !referencedTypes.ContainsKey(BaseType))
                referencedTypes.Add(BaseType, new HashSet<Symbol>());

            if (IsArray)
            {
                TypeSymbol currentSymbol = ElementType;
                while (currentSymbol.IsArray)
                    currentSymbol = currentSymbol.ElementType;
                
                if (!referencedTypes.ContainsKey(currentSymbol))
                    referencedTypes.Add(currentSymbol, new HashSet<Symbol>());
            }

            return referencedTypes;
        }

        public Symbol GetMember(ISymbol symbol, AbstractPhaseContext context)
        {
            InitSymbolDict();

            // Extension method handling
            if (symbol is IMethodSymbol methodSymbol &&
                methodSymbol.IsExtensionMethod &&
                methodSymbol.ReducedFrom != null)
            {
                symbol = methodSymbol.ReducedFrom;

                if (methodSymbol.IsGenericMethod)
                    symbol = ((IMethodSymbol)symbol).Construct(methodSymbol.TypeArguments.ToArray());
            }

            return _typeSymbols.GetOrAdd(symbol, (key) => CreateSymbol(symbol, context));
        }

        public T GetMember<T>(ISymbol symbol, AbstractPhaseContext context) where T : Symbol
        {
            return (T)GetMember(symbol, context);
        }

        public IEnumerable<T> GetMembers<T>(AbstractPhaseContext context) where T : Symbol
        {
            return GetMembers(context).OfType<T>();
        }

        public IEnumerable<Symbol> GetMembers(AbstractPhaseContext context)
        {
            List<Symbol> symbols = new List<Symbol>();
            
            foreach (ISymbol member in RoslynSymbol.GetMembers())
            {
                symbols.Add(GetMember(member, context));
            }

            return symbols;
        }

        public IEnumerable<Symbol> GetMembers(string name, AbstractPhaseContext context)
        {
            List<Symbol> symbols = new List<Symbol>();
            
            foreach (ISymbol member in RoslynSymbol.GetMembers(name))
            {
                symbols.Add(GetMember(member, context));
            }

            return symbols;
        }
        
        public IEnumerable<T> GetMembers<T>(string name, AbstractPhaseContext context) where T : Symbol
        {
            return GetMembers(name, context).OfType<T>();
        }

        public Symbol GetMember(string name, AbstractPhaseContext context)
        {
            return GetMember(RoslynSymbol.GetMembers(name).First(), context);
        }
        
        public T GetMember<T>(string name, AbstractPhaseContext context) where T : Symbol
        {
            return GetMembers<T>(name, context).FirstOrDefault();
        }

        public TypeSymbol MakeArrayType(AbstractPhaseContext context)
        {
            return context.GetTypeSymbol(context.CompileContext.RoslynCompilation.CreateArrayTypeSymbol(RoslynSymbol));
        }

        private Type _cachedType;
        private static readonly SymbolDisplayFormat _fullTypeFormat =
            new SymbolDisplayFormat(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces);

        private static readonly System.Reflection.Assembly _gameScriptAssembly =
            AppDomain.CurrentDomain.GetAssemblies().First(e => e.GetName().Name == "Assembly-CSharp");

        public static string GetFullTypeName(ITypeSymbol typeSymbol)
        {
            return typeSymbol.ToDisplayString(_fullTypeFormat);
        }
        
        public bool TryGetSystemType(out Type systemType)
        {
            if (_cachedType != null)
            {
                systemType = _cachedType;
                return true;
            }

            if (IsExtern)
            {
                _cachedType = systemType = ((ExternTypeSymbol) this).SystemType;
                return true;
            }

            int arrayDepth = 0;
            TypeSymbol currentType = this;
            while (currentType.IsArray)
            {
                arrayDepth++;
                currentType = currentType.ElementType;
            }
            
            string typeName = GetFullTypeName(currentType.RoslynSymbol);

            Type foundType = _gameScriptAssembly.GetType(typeName);

            if (foundType == null)
            {
                foreach (var udonSharpAssembly in CompilerUdonInterface.UdonSharpAssemblies)
                {
                    foundType = udonSharpAssembly.GetType(typeName);
                    if (foundType != null)
                        break;
                }
            }

            if (foundType != null)
            {
                while (arrayDepth > 0)
                {
                    arrayDepth--;
                    foundType = foundType.MakeArrayType();
                }
                
                _cachedType = systemType = foundType;
                return true;
            }

            systemType = null;
            return false;
        }

        /// <summary>
        /// Implemented by derived type symbols to create their own relevant symbol for the roslyn symbol
        /// </summary>
        /// <param name="roslynSymbol"></param>
        /// <param name="context"></param>
        /// <returns></returns>
        protected abstract Symbol CreateSymbol(ISymbol roslynSymbol, AbstractPhaseContext context);
    }
}