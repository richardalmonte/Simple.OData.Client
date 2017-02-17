﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.OData.Core;
using Microsoft.OData.Edm;
using Microsoft.Spatial;
using Simple.OData.Client.Extensions;

#pragma warning disable 1591

namespace Simple.OData.Client.V4.Adapter
{
    public class RequestWriter : RequestWriterBase
    {
        private readonly IEdmModel _model;

        public RequestWriter(ISession session, IEdmModel model, Lazy<IBatchWriter> deferredBatchWriter)
            : base(session, deferredBatchWriter)
        {
            _model = model;
        }

        protected override async Task<Stream> WriteEntryContentAsync(string method, string collection, string commandText, IDictionary<string, object> entryData, bool resultRequired)
        {
            IODataRequestMessageAsync message = IsBatch
                ? await CreateBatchOperationMessageAsync(method, collection, entryData, commandText, resultRequired).ConfigureAwait(false)
                : new ODataRequestMessage();

            if (method == RestVerbs.Get || method == RestVerbs.Delete)
                return null;

            var entityType = _model.FindDeclaredType(
                _session.Metadata.GetQualifiedTypeName(collection)) as IEdmEntityType;
            var model = (method == RestVerbs.Patch || method == RestVerbs.Merge) ? new EdmDeltaModel(_model, entityType, entryData.Keys) : _model;

            using (var messageWriter = new ODataMessageWriter(message, GetWriterSettings(), model))
            {
                var contentId = _deferredBatchWriter != null ? _deferredBatchWriter.Value.GetContentId(entryData, null) : null;
                //var entityCollection = _session.Metadata.GetEntityCollection(collection);
                var entityCollection = _session.Metadata.NavigateToCollection(collection);
                var entryDetails = _session.Metadata.ParseEntryDetails(entityCollection.Name, entryData, contentId);

                var entryWriter = await messageWriter.CreateODataEntryWriterAsync().ConfigureAwait(false);
                var entry = CreateODataEntry(entityType.FullName(), entryDetails.Properties);

                await entryWriter.WriteStartAsync(entry).ConfigureAwait(false);

                if (entryDetails.Links != null)
                {
                    foreach (var link in entryDetails.Links)
                    {
                        if (link.Value.Any(x => x.LinkData != null))
                        {
                            await WriteLinkAsync(entryWriter, entry, link.Key, link.Value).ConfigureAwait(false);
                        }
                    }
                }

                await entryWriter.WriteEndAsync().ConfigureAwait(false);
                return IsBatch ? null : await message.GetStreamAsync().ConfigureAwait(false);
            }
        }

        protected override async Task<Stream> WriteLinkContentAsync(string method, string commandText, string linkIdent)
        {
            var message = IsBatch
                ? await CreateBatchOperationMessageAsync(method, null, null, commandText, false)
.ConfigureAwait(false) : new ODataRequestMessage();

            using (var messageWriter = new ODataMessageWriter(message, GetWriterSettings(), _model))
            {
                var link = new ODataEntityReferenceLink
                {
                    Url = Utils.CreateAbsoluteUri(_session.Settings.BaseUri.AbsoluteUri, linkIdent)
                };
                await messageWriter.WriteEntityReferenceLinkAsync(link).ConfigureAwait(false);
                return IsBatch ? null : await message.GetStreamAsync().ConfigureAwait(false);
            }
        }

        protected override async Task<Stream> WriteFunctionContentAsync(string method, string commandText)
        {
            if (IsBatch)
                await CreateBatchOperationMessageAsync(method, null, null, commandText, true).ConfigureAwait(false);

            return null;
        }

        protected override async Task<Stream> WriteActionContentAsync(string method, string commandText, string actionName, string boundTypeName, IDictionary<string, object> parameters)
        {
            IODataRequestMessageAsync message = IsBatch
                ? await CreateBatchOperationMessageAsync(method, null, null, commandText, true).ConfigureAwait(false)
                : new ODataRequestMessage();

            using (var messageWriter = new ODataMessageWriter(message, GetWriterSettings(), _model))
            {
                Func<IEdmOperationParameter, IEdmType, bool> typeMatch = (parameter, baseType) =>
                    parameter == null ||
                    parameter.Type.Definition == baseType ||
                    parameter.Type.Definition.TypeKind == EdmTypeKind.Collection &&
                        (parameter.Type.Definition as IEdmCollectionType).ElementType.Definition == baseType;

                var action = boundTypeName == null
                    ? _model.SchemaElements.BestMatch(
                        x => x.SchemaElementKind == EdmSchemaElementKind.Action,
                        x => x.Name, actionName, _session.Pluralizer) as IEdmAction
                    : _model.SchemaElements.BestMatch(
                        x => x.SchemaElementKind == EdmSchemaElementKind.Action
                             && typeMatch(
                                 ((IEdmAction)x).Parameters.FirstOrDefault(p => p.Name == "bindingParameter"),
                                 _model.FindDeclaredType(boundTypeName)),
                        x => x.Name, actionName, _session.Pluralizer) as IEdmAction;
                var parameterWriter = await messageWriter.CreateODataParameterWriterAsync(action).ConfigureAwait(false);

                await parameterWriter.WriteStartAsync().ConfigureAwait(false);

                foreach (var parameter in parameters)
                {
                    var operationParameter = action.Parameters.BestMatch(x => x.Name, parameter.Key, _session.Pluralizer);
                    if (operationParameter == null)
                        throw new UnresolvableObjectException(parameter.Key, string.Format("Parameter [{0}] not found for action [{1}]", parameter.Key, actionName));

                    await WriteOperationParameterAsync(parameterWriter, operationParameter, parameter.Key, parameter.Value).ConfigureAwait(false);
                }

                await parameterWriter.WriteEndAsync().ConfigureAwait(false);
                return IsBatch ? null : await message.GetStreamAsync().ConfigureAwait(false);
            }
        }

        private async Task WriteOperationParameterAsync(ODataParameterWriter parameterWriter, IEdmOperationParameter operationParameter, string paramName, object paramValue)
        {
            switch (operationParameter.Type.Definition.TypeKind)
            {
                case EdmTypeKind.Primitive:
                case EdmTypeKind.Complex:
                    var value = GetPropertyValue(operationParameter.Type, paramValue);
                    await parameterWriter.WriteValueAsync(paramName, value).ConfigureAwait(false);
                    break;

                case EdmTypeKind.Enum:
                    await parameterWriter.WriteValueAsync(paramName, new ODataEnumValue(paramValue.ToString())).ConfigureAwait(false);
                    break;

                case EdmTypeKind.Entity:
                    var entryWriter = await parameterWriter.CreateEntryWriterAsync(paramName).ConfigureAwait(false);
                    var entry = CreateODataEntry(operationParameter.Type.Definition.FullTypeName(), paramValue.ToDictionary());
                    await entryWriter.WriteStartAsync(entry).ConfigureAwait(false);
                    await entryWriter.WriteEndAsync().ConfigureAwait(false);
                    break;

                case EdmTypeKind.Collection:
                    var collectionType = operationParameter.Type.Definition as IEdmCollectionType;
                    var elementType = collectionType.ElementType;
                    if (elementType.Definition.TypeKind == EdmTypeKind.Entity)
                    {
                        var feedWriter = await parameterWriter.CreateFeedWriterAsync(paramName).ConfigureAwait(false);
                        var feed = new ODataFeed();
                        await feedWriter.WriteStartAsync(feed).ConfigureAwait(false);
                        foreach (var item in paramValue as IEnumerable)
                        {
                            var feedEntry = CreateODataEntry(elementType.Definition.FullTypeName(), item.ToDictionary());

                            await feedWriter.WriteStartAsync(feedEntry).ConfigureAwait(false);
                            await feedWriter.WriteEndAsync().ConfigureAwait(false);
                        }
                        await feedWriter.WriteEndAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        var collectionWriter = await parameterWriter.CreateCollectionWriterAsync(paramName).ConfigureAwait(false);
                        await collectionWriter.WriteStartAsync(new ODataCollectionStart()).ConfigureAwait(false);
                        foreach (var item in paramValue as IEnumerable)
                        {
                            await collectionWriter.WriteItemAsync(item).ConfigureAwait(false);
                        }
                        await collectionWriter.WriteEndAsync().ConfigureAwait(false);
                    }
                    break;

                default:
                    throw new NotSupportedException(string.Format("Unable to write action parameter of a type {0}", operationParameter.Type.Definition.TypeKind));
            }
        }

        protected override async Task<Stream> WriteStreamContentAsync(Stream stream, bool writeAsText)
        {
            var message = new ODataRequestMessage();
            using (var messageWriter = new ODataMessageWriter(message, GetWriterSettings(ODataFormat.RawValue), _model))
            {
                var value = writeAsText ? (object)Utils.StreamToString(stream) : Utils.StreamToByteArray(stream);
                await messageWriter.WriteValueAsync(value);
                return await message.GetStreamAsync();
            }
        }

        protected override string FormatLinkPath(string entryIdent, string navigationPropertyName, string linkIdent = null)
        {
            return linkIdent == null
                ? string.Format("{0}/{1}/$ref", entryIdent, navigationPropertyName)
                : string.Format("{0}/{1}/$ref?$id={2}", entryIdent, navigationPropertyName, linkIdent);
        }

        protected override void AssignHeaders(ODataRequest request)
        {
            if (request.ResultRequired)
            {
                request.Headers.Add(HttpLiteral.Prefer, HttpLiteral.ReturnRepresentation);
            }
            else
            {
                request.Headers.Add(HttpLiteral.Prefer, HttpLiteral.ReturnMinimal);
            }
        }

        private async Task<IODataRequestMessageAsync> CreateBatchOperationMessageAsync(string method, string collection, IDictionary<string, object> entryData, string commandText, bool resultRequired)
        {
            var message = (await _deferredBatchWriter.Value.CreateOperationMessageAsync(
                Utils.CreateAbsoluteUri(_session.Settings.BaseUri.AbsoluteUri, commandText),
                method, collection, entryData, resultRequired).ConfigureAwait(false)) as IODataRequestMessageAsync;

            return message;
        }

        private async Task WriteLinkAsync(ODataWriter entryWriter, Microsoft.OData.Core.ODataEntry entry, string linkName, IEnumerable<ReferenceLink> links)
        {
            var navigationProperty = (_model.FindDeclaredType(entry.TypeName) as IEdmEntityType).NavigationProperties()
                .BestMatch(x => x.Name, linkName, _session.Pluralizer);
            bool isCollection = navigationProperty.Type.Definition.TypeKind == EdmTypeKind.Collection;

            var linkType = GetNavigationPropertyEntityType(navigationProperty);
            var linkTypeWithKey = linkType;
            while (linkTypeWithKey.DeclaredKey == null && linkTypeWithKey.BaseEntityType() != null)
            {
                linkTypeWithKey = linkTypeWithKey.BaseEntityType();
            }

            await entryWriter.WriteStartAsync(new ODataNavigationLink()
            {
                Name = linkName,
                IsCollection = isCollection,
                Url = new Uri(ODataNamespace.Related + linkType, UriKind.Absolute),
            }).ConfigureAwait(false);

            foreach (var referenceLink in links)
            {
                var linkKey = linkTypeWithKey.DeclaredKey;
                var linkEntry = referenceLink.LinkData.ToDictionary();
                var contentId = GetContentId(referenceLink);
                string linkUri;
                if (contentId != null)
                {
                    linkUri = "$" + contentId;
                }
                else
                {
                    bool isSingleton;
                    var formattedKey = _session.Adapter.GetCommandFormatter().ConvertKeyValuesToUriLiteral(
                        linkKey.ToDictionary(x => x.Name, x => linkEntry[x.Name]), true);
                    var linkedCollectionName = _session.Metadata.GetLinkedCollectionName(
                        referenceLink.LinkData.GetType().Name, linkTypeWithKey.Name, out isSingleton);
                    linkUri = linkedCollectionName + (isSingleton ? string.Empty : formattedKey);
                }
                var link = new ODataEntityReferenceLink
                {
                    Url = Utils.CreateAbsoluteUri(_session.Settings.BaseUri.AbsoluteUri, linkUri)
                };

                await entryWriter.WriteEntityReferenceLinkAsync(link).ConfigureAwait(false);
            }

            await entryWriter.WriteEndAsync().ConfigureAwait(false);
        }

        private static IEdmEntityType GetNavigationPropertyEntityType(IEdmNavigationProperty navigationProperty)
        {
            if (navigationProperty.Type.Definition.TypeKind == EdmTypeKind.Collection)
                return (navigationProperty.Type.Definition as IEdmCollectionType).ElementType.Definition as IEdmEntityType;
            else
                return navigationProperty.Type.Definition as IEdmEntityType;
        }

        private ODataMessageWriterSettings GetWriterSettings(ODataFormat preferredContentType = null)
        {
            var settings = new ODataMessageWriterSettings()
            {
                ODataUri = new ODataUri()
                {
                    RequestUri = _session.Settings.BaseUri,
                },
                Indent = true,
                DisableMessageStreamDisposal = !IsBatch,
            };
            ODataFormat contentType;
            if (preferredContentType != null)
            {
                contentType = preferredContentType;
            }
            else
            {
                switch (_session.Settings.PayloadFormat)
                {
                    case ODataPayloadFormat.Atom:
#pragma warning disable 0618
                        contentType = ODataFormat.Atom;
#pragma warning restore 0618
                        break;
                    case ODataPayloadFormat.Json:
                    default:
                        contentType = ODataFormat.Json;
                        break;
                }
            }
            settings.SetContentType(contentType);
            return settings;
        }

        private Microsoft.OData.Core.ODataEntry CreateODataEntry(string typeName, IDictionary<string, object> properties)
        {
            var entry = new Microsoft.OData.Core.ODataEntry() { TypeName = typeName };

            var typeProperties = (_model.FindDeclaredType(entry.TypeName) as IEdmEntityType).Properties();
            Func<string, string> findMatchingPropertyName = name =>
            {
                var property = typeProperties.BestMatch(y => y.Name, name, _session.Pluralizer);
                return property != null ? property.Name : name;
            };
            entry.Properties = properties.Select(x => new ODataProperty()
            {
                Name = findMatchingPropertyName(x.Key),
                Value = GetPropertyValue(typeProperties, x.Key, x.Value)
            }).ToList();

            return entry;
        }

        private object GetPropertyValue(IEnumerable<IEdmProperty> properties, string key, object value)
        {
            var property = properties.BestMatch(x => x.Name, key, _session.Pluralizer);
            return property != null ? GetPropertyValue(property.Type, value) : value;
        }

        private object GetPropertyValue(IEdmTypeReference propertyType, object value)
        {
            if (value == null)
                return value;

            switch (propertyType.TypeKind())
            {
                case EdmTypeKind.Complex:
                    if (CustomConverters.HasObjectConverter(value.GetType()))
                    {
                        return CustomConverters.Convert(value, value.GetType());
                    }
                    var complexTypeProperties = propertyType.AsComplex().StructuralProperties();
                    return new ODataComplexValue
                    {
                        TypeName = propertyType.FullName(),
                        Properties = value.ToDictionary()
                            .Where(val => complexTypeProperties.Any(p => p.Name == val.Key))
                            .Select(x => new ODataProperty
                            {
                                Name = x.Key,
                                Value = GetPropertyValue(complexTypeProperties, x.Key, x.Value),
                            })
                    };

                case EdmTypeKind.Collection:
                    var collection = propertyType.AsCollection();
                    return new ODataCollectionValue()
                    {
                        TypeName = propertyType.FullName(),
                        Items = ((IEnumerable)value).Cast<object>().Select(x => GetPropertyValue(collection.ElementType(), x)),
                    };

                case EdmTypeKind.Primitive:
                    var mappedTypes = _typeMap.Where(x => x.Value == (propertyType.Definition as IEdmPrimitiveType).PrimitiveKind);
                    if (mappedTypes.Any())
                    {
                        foreach (var mappedType in mappedTypes)
                        {
                            object result;
                            if (Utils.TryConvert(value, mappedType.Key, out result))
                                return result;
                        }
                        throw new NotSupportedException(string.Format("Conversion is not supported from type {0} to OData type {1}", value.GetType(), propertyType));
                    }
                    return value;

                case EdmTypeKind.Enum:
                    return new ODataEnumValue(value.ToString());

                case EdmTypeKind.None:
                    if (CustomConverters.HasObjectConverter(value.GetType()))
                    {
                        return CustomConverters.Convert(value, value.GetType());
                    }
                    throw new NotSupportedException(string.Format("Conversion is not supported from type {0} to OData type {1}", value.GetType(), propertyType));

                default:
                    return value;
            }
        }

        private static readonly Dictionary<Type, EdmPrimitiveTypeKind> _typeMap = new[]
            {
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(string), EdmPrimitiveTypeKind.String),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(bool), EdmPrimitiveTypeKind.Boolean),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(bool?), EdmPrimitiveTypeKind.Boolean),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(byte), EdmPrimitiveTypeKind.Byte),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(byte?), EdmPrimitiveTypeKind.Byte),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(decimal), EdmPrimitiveTypeKind.Decimal),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(decimal?), EdmPrimitiveTypeKind.Decimal),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(double), EdmPrimitiveTypeKind.Double),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(double?), EdmPrimitiveTypeKind.Double),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(Guid), EdmPrimitiveTypeKind.Guid),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(Guid?), EdmPrimitiveTypeKind.Guid),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(short), EdmPrimitiveTypeKind.Int16),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(short?), EdmPrimitiveTypeKind.Int16),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(int), EdmPrimitiveTypeKind.Int32),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(int?), EdmPrimitiveTypeKind.Int32),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(long), EdmPrimitiveTypeKind.Int64),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(long?), EdmPrimitiveTypeKind.Int64),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(sbyte), EdmPrimitiveTypeKind.SByte),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(sbyte?), EdmPrimitiveTypeKind.SByte),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(float), EdmPrimitiveTypeKind.Single),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(float?), EdmPrimitiveTypeKind.Single),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(byte[]), EdmPrimitiveTypeKind.Binary),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(Stream), EdmPrimitiveTypeKind.Stream),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(Geography), EdmPrimitiveTypeKind.Geography),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(GeographyPoint), EdmPrimitiveTypeKind.GeographyPoint),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(GeographyLineString), EdmPrimitiveTypeKind.GeographyLineString),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(GeographyPolygon), EdmPrimitiveTypeKind.GeographyPolygon),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(GeographyCollection), EdmPrimitiveTypeKind.GeographyCollection),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(GeographyMultiLineString), EdmPrimitiveTypeKind.GeographyMultiLineString),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(GeographyMultiPoint), EdmPrimitiveTypeKind.GeographyMultiPoint),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(GeographyMultiPolygon), EdmPrimitiveTypeKind.GeographyMultiPolygon),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(Geometry), EdmPrimitiveTypeKind.Geometry),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(GeometryPoint), EdmPrimitiveTypeKind.GeometryPoint),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(GeometryLineString), EdmPrimitiveTypeKind.GeometryLineString),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(GeometryPolygon), EdmPrimitiveTypeKind.GeometryPolygon),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(GeometryCollection), EdmPrimitiveTypeKind.GeometryCollection),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(GeometryMultiLineString), EdmPrimitiveTypeKind.GeometryMultiLineString),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(GeometryMultiPoint), EdmPrimitiveTypeKind.GeometryMultiPoint),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(GeometryMultiPolygon), EdmPrimitiveTypeKind.GeometryMultiPolygon),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(DateTimeOffset), EdmPrimitiveTypeKind.DateTimeOffset),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(DateTimeOffset?), EdmPrimitiveTypeKind.DateTimeOffset),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(TimeSpan), EdmPrimitiveTypeKind.Duration),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(TimeSpan?), EdmPrimitiveTypeKind.Duration),

                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(XElement), EdmPrimitiveTypeKind.String),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(ushort), EdmPrimitiveTypeKind.Int32),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(ushort?), EdmPrimitiveTypeKind.Int32),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(uint), EdmPrimitiveTypeKind.Int64),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(uint?), EdmPrimitiveTypeKind.Int64),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(ulong), EdmPrimitiveTypeKind.Int64),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(ulong?), EdmPrimitiveTypeKind.Int64),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(char[]), EdmPrimitiveTypeKind.String),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(char), EdmPrimitiveTypeKind.String),
                new KeyValuePair<Type, EdmPrimitiveTypeKind>(typeof(char?), EdmPrimitiveTypeKind.String),
            }
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }
}