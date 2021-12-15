﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using Juxtapose.SourceGenerator.Model;

using Microsoft.CodeAnalysis;

namespace Juxtapose.SourceGenerator.CodeGenerate
{
    public class IllusionClassCodeGenerator : ISourceCodeProvider<SourceCode>
    {
        #region Private 字段

        private readonly ClassStringBuilder _sourceBuilder = new();

        private readonly VariableName _vars;

        private string? _generatedSource = null;

        #endregion Private 字段

        #region Public 属性

        public Accessibility Accessibility { get; }

        public JuxtaposeSourceGeneratorContext Context { get; }

        public INamedTypeSymbol ContextTypeSymbol { get; }

        public INamedTypeSymbol ImplementTypeSymbol { get; }

        /// <summary>
        /// 继承的类型
        /// </summary>
        public INamedTypeSymbol? InheritTypeSymbol { get; }

        public string Namespace { get; }

        public string SourceHintName { get; }

        public string TypeFullName { get; }

        public string TypeName { get; }

        #endregion Public 属性

        #region Public 构造函数

        public IllusionClassCodeGenerator(JuxtaposeSourceGeneratorContext context, IllusionAttributeDefine attributeDefine, INamedTypeSymbol contextTypeSymbol)
        {
            Context = context ?? throw new ArgumentNullException(nameof(context));
            ContextTypeSymbol = contextTypeSymbol ?? throw new ArgumentNullException(nameof(contextTypeSymbol));

            ImplementTypeSymbol = attributeDefine.TargetType ?? throw new ArgumentNullException(nameof(attributeDefine.TargetType));
            InheritTypeSymbol = attributeDefine.InheritType;

            var implementTypeFullName = ImplementTypeSymbol.ToDisplayString();
            Namespace = implementTypeFullName.Substring(0, implementTypeFullName.LastIndexOf('.'));

            if (attributeDefine.GeneratedTypeName is not string proxyTypeName
                || string.IsNullOrWhiteSpace(proxyTypeName))
            {
                TypeFullName = InheritTypeSymbol is null
                               ? $"{implementTypeFullName}Illusion"
                               : $"{implementTypeFullName}As{InheritTypeSymbol.Name}Illusion";
                TypeName = TypeFullName.Substring(Namespace.Length + 1);
            }
            else
            {
                if (proxyTypeName.Contains('.'))
                {
                    TypeName = proxyTypeName.Substring(proxyTypeName.LastIndexOf('.') + 1);
                    TypeFullName = proxyTypeName;
                    Namespace = proxyTypeName.Substring(0, proxyTypeName.LastIndexOf('.'));
                }
                else
                {
                    TypeName = proxyTypeName;
                    TypeFullName = $"{Namespace}.{proxyTypeName}";
                }
            }

            Accessibility = attributeDefine.Accessibility switch
            {
                GeneratedAccessibility.InheritContext => contextTypeSymbol.DeclaredAccessibility,
                GeneratedAccessibility.InheritBase => InheritTypeSymbol?.DeclaredAccessibility ?? Accessibility.Public,
                GeneratedAccessibility.Public => Accessibility.Public,
                GeneratedAccessibility.Internal => Accessibility.Internal,
                _ => ImplementTypeSymbol.DeclaredAccessibility,
            };

            SourceHintName = $"{TypeFullName}.Illusion.g.cs";

            _vars = new VariableName()
            {
                Executor = "Executor",
            };
        }

        #endregion Public 构造函数

        #region Private 方法

        private void GenerateConstructorProxyCode()
        {
            var implTypeName = ImplementTypeSymbol.Name;
            foreach (var constructor in ImplementTypeSymbol.Constructors.Where(m => m.NotStatic()))
            {
                if (!Context.TryGetParameterPackWithDiagnostic(constructor, out var parameterPackSourceCode))
                {
                    continue;
                }
                var paramPackContext = constructor.GetParamPackContext();

                var ctorAnnotation = $"/// <inheritdoc cref=\"{implTypeName}.{implTypeName}({string.Join(", ", constructor.Parameters.Select(m => m.Type.ToFullyQualifiedDisplayString()))})\"/>";

                var ctorArguments = constructor.GenerateMethodArgumentString();
                var accessibility = constructor.GetAccessibilityCodeString();

                _sourceBuilder.AppendIndentLine(ctorAnnotation);
                _sourceBuilder.AppendIndentLine($"[Obsolete(\"Use static method \\\"{TypeName}.NewAsync()\\\" instead of sync constructor.\")]");
                _sourceBuilder.AppendIndentLine($"{accessibility} {TypeName}({ctorArguments})");
                _sourceBuilder.Scope(() =>
                {
                    paramPackContext.GenParamPackCode(_sourceBuilder, "parameterPack");

                    _sourceBuilder.AppendLine(@"
var (executorOwner, instanceId) = CreateObjectAsync(parameterPack, true, CancellationToken.None).GetAwaiter().GetResult();

_executorOwner = executorOwner;
_instanceId = instanceId;

_executor = _executorOwner.Executor;

_runningTokenRegistration = _executorOwner.Executor.RunningToken.Register(Dispose);
_runningTokenSource = new CancellationTokenSource();
_runningToken = _runningTokenSource.Token;");
                });
                _sourceBuilder.AppendLine();

                _sourceBuilder.AppendIndentLine(ctorAnnotation);
                _sourceBuilder.AppendIndentLine($"{accessibility} static async Task<{TypeName}> NewAsync({ctorArguments}{(ctorArguments.Length > 0 ? ", " : string.Empty)}CancellationToken cancellation = default)");
                _sourceBuilder.Scope(() =>
                {
                    paramPackContext.GenParamPackCode(_sourceBuilder, "parameterPack");

                    _sourceBuilder.AppendLine($@"
var (executorOwner, instanceId) = await CreateObjectAsync(parameterPack, false, cancellation);
return new {TypeName}(executorOwner, instanceId);");
                });
                _sourceBuilder.AppendLine();
            }

            _sourceBuilder.AppendLine($@"public {TypeName}({TypeFullNames.Juxtapose.IJuxtaposeExecutorOwner} executorOwner, int instanceId)
{{
    _executorOwner = executorOwner;
    _instanceId = instanceId;

    _executor = _executorOwner.Executor;

    _runningTokenRegistration = _executorOwner.Executor.RunningToken.Register(Dispose);
    _runningTokenSource = new CancellationTokenSource();
    _runningToken = _runningTokenSource.Token;
}}");

            _sourceBuilder.AppendLine();

            _sourceBuilder.AppendLine($"private static async {TypeFullNames.System.Threading.Tasks.Task}<({TypeFullNames.Juxtapose.IJuxtaposeExecutorOwner} executorOwner, int instanceId)> CreateObjectAsync<TParameterPack>(TParameterPack parameterPack, bool noContext = true, {TypeFullNames.System.Threading.CancellationToken} cancellation = default) where TParameterPack : class");
            _sourceBuilder.Scope(() =>
            {
                _sourceBuilder.AppendLine($@"if (noContext)
{{
    await global::Juxtapose.SynchronizationContextRemover.Awaiter;
}}

var executorOwner = await {ContextTypeSymbol.ToFullyQualifiedDisplayString()}.SharedInstance.GetExecutorOwnerAsync(s__creationContext, cancellation);
var executor = executorOwner.Executor;

var instanceId = executor.InstanceIdGenerator.Next();
var message = new global::Juxtapose.Messages.CreateObjectInstanceMessage<TParameterPack>(instanceId) {{ ParameterPack = parameterPack }};

try
{{
    await executorOwner.Executor.InvokeMessageAsync(message, cancellation);
}}
catch
{{
    executorOwner.Dispose();
    throw;
}}

return (executorOwner, instanceId);");
            });
        }

        private void GenerateProxyClassSource()
        {
            _sourceBuilder.AppendLine(Constants.JuxtaposeGenerateCodeHeader);
            _sourceBuilder.AppendLine();

            _sourceBuilder.Namespace(() =>
            {
                _sourceBuilder.AppendIndentLine($"/// <inheritdoc cref=\"{ImplementTypeSymbol.ToFullyQualifiedDisplayString()}\"/>");

                var inheritBaseCodeSnippet = InheritTypeSymbol is null
                                             ? string.Empty
                                             : $"{InheritTypeSymbol.ToFullyQualifiedDisplayString()}, ";

                _sourceBuilder.AppendIndentLine($"{Accessibility.ToCodeString()} sealed partial class {TypeName} : {inheritBaseCodeSnippet}global::Juxtapose.IIllusion, {TypeFullNames.System.IDisposable}");

                _sourceBuilder.Scope(() =>
                {
                    _sourceBuilder.AppendIndentLine($"private static readonly {TypeFullNames.Juxtapose.ExecutorCreationContext} s__creationContext = new(typeof({ImplementTypeSymbol.ToFullyQualifiedDisplayString()}), \"ctor\", false, true);", true);

                    _sourceBuilder.AppendLine($@"
private {TypeFullNames.Juxtapose.IJuxtaposeExecutorOwner} _executorOwner;

private {TypeFullNames.Juxtapose.JuxtaposeExecutor} {_vars.Executor} => _executorOwner.Executor;

private readonly int _instanceId;

private global::System.Threading.CancellationTokenSource _runningTokenSource;

private readonly global::System.Threading.CancellationToken _runningToken;

private CancellationTokenRegistration? _runningTokenRegistration;

#region IIllusion

private readonly global::Juxtapose.JuxtaposeExecutor _executor;

/// <inheritdoc/>
JuxtaposeExecutor IIllusion.Executor => _executor;

/// <inheritdoc/>
bool IIllusion.IsAvailable => !_isDisposed;

#endregion IIllusion

");
                    _sourceBuilder.AppendLine();

                    GenerateConstructorProxyCode();

                    new InstanceProxyCodeGenerator(Context, _sourceBuilder, InheritTypeSymbol ?? ImplementTypeSymbol, new(_vars) { MethodBodyPrefixSnippet = "ThrowIfDisposed();" }).GenerateMemberProxyCode();

                    _sourceBuilder.AppendLine();

                    _sourceBuilder.AppendLine($@"private bool _isDisposed = false;

private void ThrowIfDisposed()
{{
    if (_isDisposed)
    {{
        throw new ObjectDisposedException(""_executorOwner"");
    }}
}}

~{TypeName}()
{{
    Dispose();
}}

public void Dispose()
{{
    if (_isDisposed)
    {{
        return;
    }}
    _isDisposed = true;
    {_vars.Executor}.DisposeObjectInstance(_instanceId);
    _runningTokenSource.Cancel();
    _runningTokenSource.Dispose();
    _executorOwner.Dispose();
    _runningTokenRegistration?.Dispose();
    _executorOwner = null!;
    _runningTokenSource = null!;
    _runningTokenRegistration = null;
    global::System.GC.SuppressFinalize(this);
}}");
                });
            }, Namespace);
        }

        private string GenerateProxyTypeSource()
        {
            if (_generatedSource != null)
            {
                return _generatedSource;
            }

            GenerateProxyClassSource();
            _generatedSource = _sourceBuilder.ToString();

            return _generatedSource;
        }

        #endregion Private 方法

        #region Public 方法

        public IEnumerable<SourceCode> GetSources()
        {
            if (!Context.TryAddImplementInherit(ImplementTypeSymbol, InheritTypeSymbol))
            {
                Context.GeneratorExecutionContext.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.MultipleIllusionClassDefine, null, ImplementTypeSymbol, InheritTypeSymbol));
                yield break;
            }
            //HACK 暂不处理嵌套委托
            var delegateSymbols = (InheritTypeSymbol ?? ImplementTypeSymbol).GetProxyableMembers()
                                                                            .OfType<IMethodSymbol>()
                                                                            .SelectMany(m => m.Parameters)
                                                                            .Where(m => m.Type.IsDelegate())
                                                                            .Select(m => m.Type)
                                                                            .OfType<INamedTypeSymbol>()
                                                                            .Select(m => m.DelegateInvokeMethod)
                                                                            .Distinct(SymbolEqualityComparer.Default)
                                                                            .OfType<IMethodSymbol>()
                                                                            .ToArray();

            var members = (InheritTypeSymbol ?? ImplementTypeSymbol).GetProxyableMembers()
                                                                    .Concat(ImplementTypeSymbol.Constructors.Where(m => m.NotStatic()))
                                                                    .Concat(delegateSymbols)
                                                                    .Distinct(SymbolEqualityComparer.Default)
                                                                    .OfType<IMethodSymbol>()
                                                                    .ToArray();

            Context.TryAddInterfaceMethods(InheritTypeSymbol, InheritTypeSymbol.GetProxyableMembers().GetMethodSymbols());

            var parameterPackCodeGenerator = new MethodParameterPackCodeGenerator(Context, members, Namespace, $"{InheritTypeSymbol.ToDisplayString()}.ParameterPack.g.cs");
            var parameterPackTypeSources = parameterPackCodeGenerator.GetSources()
                                                                     .ToList();

            Context.TryAddConstructorMethods(ImplementTypeSymbol, ImplementTypeSymbol.Constructors.Where(m => m.NotStatic()).ToImmutableArray());

            Context.TryAddMethodArgumentPackSourceCodes(parameterPackTypeSources);

            foreach (var parameterPackTypeSource in parameterPackTypeSources)
            {
                yield return parameterPackTypeSource;
            }

            yield return new FullSourceCode(SourceHintName, GenerateProxyTypeSource());
        }

        #endregion Public 方法
    }
}