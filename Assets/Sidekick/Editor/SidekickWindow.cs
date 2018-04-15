﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Sabresaurus.Sidekick.Requests;
using Sabresaurus.Sidekick.Responses;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEditor.Networking.PlayerConnection;
using UnityEngine;
using UnityEngine.Networking.PlayerConnection;

namespace Sabresaurus.Sidekick
{
    public enum InspectionConnection { LocalEditor, RemotePlayer };

    public class SidekickWindow : EditorWindow
    {
        const float AUTO_REFRESH_FREQUENCY = 2f;

        // Shared serialized context that persists through recompiles
        SidekickSettings settings = new SidekickSettings();

        WrappedMethod expandedMethod = null;
        List<WrappedVariable> arguments = null;

        Vector2 scrollPosition = Vector2.zero;

        TreeViewState treeViewState;

        SimpleTreeView treeView;
        SearchField hierarchySearchField;
        SearchField searchField2;

        GetGameObjectResponse gameObjectResponse;

        double timeLastRefreshed = 0;

        int lastRequestID = 0;


        [MenuItem("Tools/Sidekick")]
        static void Init()
        {
            SidekickWindow window = (SidekickWindow)EditorWindow.GetWindow(typeof(SidekickWindow));
            window.Show();
            window.titleContent = new GUIContent("Sidekick");
            window.UpdateTitleContent();
        }

        void UpdateTitleContent()
        {
            string[] guids = AssetDatabase.FindAssets("SidekickIcon t:Texture");
            if (guids.Length >= 1)
            {
                Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath(guids[0]));
                titleContent = new GUIContent("Sidekick", texture);
            }
            else
            {
                titleContent = new GUIContent("Sidekick");
            }
        }

        void OnEnable()
        {
            UpdateTitleContent();

            EditorConnection.instance.Initialize();
            EditorConnection.instance.Register(RuntimeSidekick.kMsgSendPlayerToEditor, OnMessageEvent);

            // Check if we already had a serialized view state (state 
            // that survived assembly reloading)
            if (treeViewState == null)
            {
                treeViewState = new TreeViewState();

            }

            treeView = new SimpleTreeView(treeViewState);
            treeView.OnSelectionChanged += OnHierarchySelectionChanged;

            searchField2 = new SearchField();

            hierarchySearchField = new SearchField();
            hierarchySearchField.downOrUpArrowKeyPressed += treeView.SetFocusAndEnsureSelectedItem;
        }

        void OnDisable()
        {
            EditorConnection.instance.Unregister(RuntimeSidekick.kMsgSendPlayerToEditor, OnMessageEvent);
            EditorConnection.instance.DisconnectAll();
        }

        void FetchSelectionComponents()
        {
            IList<int> selectedIds = treeView.GetSelection();
            if (selectedIds.Count >= 1)
            {
                IList<TreeViewItem> items = treeView.GetRows();
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i].id == selectedIds[0])
                    {
                        // Get the path of the selection
                        string path = GetPathForTreeViewItem(items[i]);
                        //Debug.Log(TransformHelper.GetFromPath(path).name);
                        SendToPlayers(new GetGameObjectRequest(path, settings.GetGameObjectFlags));
                        break;
                    }
                }
            }
        }

        void OnHierarchySelectionChanged(IList<int> selectedIds)
        {
            FetchSelectionComponents();
        }

        private void OnMessageEvent(MessageEventArgs args)
        {
            BaseResponse response = SidekickResponseProcessor.Process(args.data);

            if (response is GetHierarchyResponse)
            {
                GetHierarchyResponse hierarchyResponse = (GetHierarchyResponse)response;
                List<TreeViewItem> displays = new List<TreeViewItem>();
                int index = 0;
                foreach (var scene in hierarchyResponse.Scenes)
                {
                    displays.Add(new TreeViewItem { id = index, depth = 0, displayName = scene.SceneName });
                    index++;

                    foreach (var node in scene.HierarchyNodes)
                    {
                        displays.Add(new TreeViewItem { id = index, depth = node.Depth + 1, displayName = node.ObjectName });
                        index++;
                    }
                }

                treeView.SetDisplays(displays);

            }
            else if (response is GetGameObjectResponse)
            {
                gameObjectResponse = (GetGameObjectResponse)response;
                StringBuilder stringBuilder = new StringBuilder();
                stringBuilder.AppendLine(gameObjectResponse.GameObjectName);
                foreach (var component in gameObjectResponse.Components)
                {
                    stringBuilder.Append(" ");
                    stringBuilder.AppendLine(component.TypeName);
                    foreach (var field in component.Fields)
                    {
                        stringBuilder.Append("  ");
                        stringBuilder.Append(field.VariableName);
                        stringBuilder.Append(" ");
                        stringBuilder.Append(field.DataType);
                        stringBuilder.Append(" = ");
                        stringBuilder.Append(field.Value);
                        stringBuilder.AppendLine();
                    }
                    foreach (var property in component.Properties)
                    {
                        stringBuilder.Append("  ");
                        stringBuilder.Append(property.VariableName);
                        stringBuilder.Append(" ");
                        stringBuilder.Append(property.DataType);
                        stringBuilder.Append(" = ");
                        stringBuilder.Append(property.Value);
                        stringBuilder.AppendLine();
                    }
                    foreach (var method in component.Methods)
                    {
                        stringBuilder.Append("  ");
                        stringBuilder.Append(method.MethodName);
                        stringBuilder.Append(" ");
                        stringBuilder.Append(method.ReturnType);
                        stringBuilder.Append(" ");
                        stringBuilder.Append(method.ParameterCount);
                        stringBuilder.Append(" ");
                        if (method.Parameters.Count > 0)
                        {
                            stringBuilder.Append(method.Parameters[0].DataType);
                        }
                        stringBuilder.AppendLine();
                    }
                }
                //Debug.Log(stringBuilder);
            }
            else if (response is InvokeMethodResponse)
            {
                InvokeMethodResponse invokeMethodResponse = (InvokeMethodResponse)response;
                Debug.Log(invokeMethodResponse.MethodName + "() returned " + invokeMethodResponse.ReturnedVariable.Value);
            }
            else if (response is GetUnityObjectsResponse)
            {
                GetUnityObjectsResponse castResponse = (GetUnityObjectsResponse)response;

                RemotePickerWindow.Show(castResponse.ComponentDescription, castResponse.ObjectDescriptions, castResponse.Variable, OnObjectPickerChanged);
            }
        }



        private void OnInspectorUpdate()
        {
            if (settings.InspectionConnection == InspectionConnection.LocalEditor
                || settings.AutoRefreshRemote)
            {
                if (EditorApplication.timeSinceStartup > timeLastRefreshed + AUTO_REFRESH_FREQUENCY)
                {
                    timeLastRefreshed = EditorApplication.timeSinceStartup;
                    SendToPlayers(new GetHierarchyRequest());
                    FetchSelectionComponents();
                }
            }
        }


        void OnGUI()
        {
			GUILayout.Space(9);
            GUILayout.BeginHorizontal();
            // Column 1
            GUILayout.BeginVertical(GUILayout.Width(position.width / 2f));

            settings.InspectionConnection = (InspectionConnection)GUILayout.Toolbar((int)settings.InspectionConnection, new string[] { "Local", "Remote" }, new GUIStyle("LargeButton"));

            if (settings.InspectionConnection == InspectionConnection.RemotePlayer)
            {
                int playerCount = EditorConnection.instance.ConnectedPlayers.Count;


                StringBuilder builder = new StringBuilder();
                builder.AppendLine(string.Format("{0} players connected.", playerCount));
                int count = 0;
                foreach (ConnectedPlayer p in EditorConnection.instance.ConnectedPlayers)
                {
                    builder.AppendLine(string.Format("[{0}] - {1} {2}", count++, p.name, p.playerId));
                }
                EditorGUILayout.HelpBox(builder.ToString(), MessageType.Info);
                settings.AutoRefreshRemote = EditorGUILayout.Toggle("Auto Refresh Remote", settings.AutoRefreshRemote);
            }

            //localDevMode = EditorGUILayout.Toggle("Local Dev Mode", localDevMode);
            settings.GetGameObjectFlags = (InfoFlags)EditorGUILayout.EnumFlagsField(settings.GetGameObjectFlags);


            if (GUILayout.Button("Refresh Hierarchy"))
            {
                SendToPlayers(new GetHierarchyRequest());
            }


            //EditorGUILayout.TextArea(lastDebugText, GUILayout.ExpandHeight(true), GUILayout.MinHeight(300));
            DoToolbar();
            DoTreeView();

            GUILayout.EndVertical();
            Rect verticalLineRect = new Rect(position.width / 2f - 1, 0, 1, position.height);
            GUI.color = new Color(0.5f, 0.5f, 0.5f);
            GUI.DrawTexture(verticalLineRect, EditorGUIUtility.whiteTexture);
            GUI.color = Color.white;

            // Column 2
            GUILayout.BeginVertical();
            GUILayout.Space(2);

            settings.SearchTerm = searchField2.OnGUI(settings.SearchTerm);
            GUILayout.Space(3);

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            if (gameObjectResponse != null)
            {
				string activeSearchTerm = settings.SearchTerm;

                foreach (var component in gameObjectResponse.Components)
                {
                    GUIStyle style = new GUIStyle(EditorStyles.foldout);
                    style.fontStyle = FontStyle.Bold;

                    Texture icon = IconLookup.GetIcon(component.TypeName);
                    GUIContent content = new GUIContent(component.TypeName, icon, "Instance ID: " + component.InstanceID.ToString());
                    float labelWidth = EditorGUIUtility.labelWidth; // Cache label width
                    // Temporarily set the label width to full width so the icon is not squashed with long strings
                    EditorGUIUtility.labelWidth = position.width / 2f;
                    EditorGUILayout.Foldout(true, content, style);

                    EditorGUIUtility.labelWidth = labelWidth; // Restore label width
                    foreach (var field in component.Fields)
                    {
                        if(!string.IsNullOrEmpty(activeSearchTerm) && !field.VariableName.Contains(activeSearchTerm, StringComparison.InvariantCultureIgnoreCase))
                        {
                            // Active search term not matched, skip it
                            continue;
                        }
                        EditorGUI.BeginChangeCheck();
                        object newValue = VariableDrawer.Draw(component, field, OnOpenObjectPicker);
                        if (EditorGUI.EndChangeCheck() && (field.Attributes & VariableAttributes.ReadOnly) == VariableAttributes.None)
                        {
                            if (newValue != field.Value)
                            {
                                field.Value = newValue;
                                SendToPlayers(new SetVariableRequest(component.InstanceID, field));
                            }

                            //Debug.Log("Value changed in " + field.VariableName);
                        }
                    }
                    foreach (var property in component.Properties)
                    {
                        if (!string.IsNullOrEmpty(activeSearchTerm) && !property.VariableName.Contains(activeSearchTerm, StringComparison.InvariantCultureIgnoreCase))
                        {
                            // Active search term not matched, skip it
                            continue;
                        }
                        EditorGUI.BeginChangeCheck();
                        object newValue = VariableDrawer.Draw(component, property, OnOpenObjectPicker);
                        if (EditorGUI.EndChangeCheck() && (property.Attributes & VariableAttributes.ReadOnly) == VariableAttributes.None)
                        {
                            if (newValue != property.Value)
                            {
                                property.Value = newValue;
                                SendToPlayers(new SetVariableRequest(component.InstanceID, property));
                            }
                            //Debug.Log("Value changed in " + property.VariableName);
                        }
                    }

                    GUIStyle expandButtonStyle = new GUIStyle(GUI.skin.button);
                    RectOffset padding = expandButtonStyle.padding;
                    padding.left = 0;
                    padding.right = 1;
                    expandButtonStyle.padding = padding;

                    foreach (var method in component.Methods)
                    {
                        if (!string.IsNullOrEmpty(activeSearchTerm) && !method.MethodName.Contains(activeSearchTerm, StringComparison.InvariantCultureIgnoreCase))
                        {
                            // Active search term not matched, skip it
                            continue;
                        }
                        GUILayout.BeginHorizontal();
                        //if (method.ReturnType == typeof(void))
                        //    labelStyle.normal.textColor = Color.grey;
                        //else if (method.ReturnType.IsValueType)
                        //    labelStyle.normal.textColor = new Color(0, 0, 1);
                        //else
                        //labelStyle.normal.textColor = new Color32(255, 130, 0, 255);

                        if (GUILayout.Button(TypeUtility.NameForType(method.ReturnType) + " " + method.MethodName + " (" + method.ParameterCount + ")"))
                        {
                            List<WrappedVariable> defaultArguments = new List<WrappedVariable>();

                            for (int i = 0; i < method.ParameterCount; i++)
                            {
                                Type type = DataTypeHelper.GetSystemTypeFromWrappedDataType(method.Parameters[i].DataType);
                                object defaultValue = TypeUtility.GetDefaultValue(type);

                                WrappedParameter parameter = method.Parameters[i];
                                defaultArguments.Add(new WrappedVariable(parameter.VariableName, defaultValue, type, false));
                            }

                            SendToPlayers(new InvokeMethodRequest(component.InstanceID, method.MethodName, defaultArguments.ToArray()));
                        }

                        bool wasExpanded = (expandedMethod == method);
                        bool expanded = GUILayout.Toggle(wasExpanded, "▼", expandButtonStyle, GUILayout.Width(20));
                        GUILayout.EndHorizontal();
                        if (expanded != wasExpanded) // has changed
                        {
                            if (expanded)
                            {
                                expandedMethod = method;
                                arguments = new List<WrappedVariable>(method.ParameterCount);
                                for (int i = 0; i < method.ParameterCount; i++)
                                {
                                    Type type = DataTypeHelper.GetSystemTypeFromWrappedDataType(method.Parameters[i].DataType);
                                    object defaultValue = TypeUtility.GetDefaultValue(type);

                                    WrappedParameter parameter = method.Parameters[i];
                                    arguments.Add(new WrappedVariable(parameter.VariableName, defaultValue, type, false));
                                }
                            }
                            else
                            {
                                expandedMethod = null;
                                arguments = null;
                            }
                        }
                        else if (expanded)
                        {
                            EditorGUI.indentLevel++;
                            foreach (var argument in arguments)
                            {
                                argument.Value = VariableDrawer.DrawIndividualVariable(null, argument, argument.VariableName, DataTypeHelper.GetSystemTypeFromWrappedDataType(argument.DataType), argument.Value, OnOpenObjectPicker);
                            }

                            Rect buttonRect = GUILayoutUtility.GetRect(new GUIContent(), GUI.skin.button);
                            buttonRect = EditorGUI.IndentedRect(buttonRect);

                            if (GUI.Button(buttonRect, "Fire"))
                            {
                                SendToPlayers(new InvokeMethodRequest(component.InstanceID, method.MethodName, arguments.ToArray()));
                            }
                            EditorGUI.indentLevel--;

                            GUILayout.Space(10);
                        }
                    }
                    Rect rect = GUILayoutUtility.GetRect(new GUIContent(), GUI.skin.label, GUILayout.ExpandWidth(true), GUILayout.Height(1));
                    rect.xMin -= 10;
                    rect.xMax += 10;
                    GUI.color = new Color(0.5f, 0.5f, 0.5f);
                    GUI.DrawTexture(rect, EditorGUIUtility.whiteTexture);
                    GUI.color = Color.white;
                }
            }
            EditorGUILayout.EndScrollView();

            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }

        static string GetPathForTreeViewItem(TreeViewItem item)
        {
            string path = item.displayName;

            item = item.parent;
            while (item != null && item.depth >= 0)
            {
                path = item.displayName + "/" + path;
                item = item.parent;
            }

            return path;
        }

        public void OnOpenObjectPicker(ComponentDescription componentDescription, WrappedVariable variable)
        {
            SendToPlayers(new GetUnityObjectsRequest(variable, componentDescription));
        }

        public void OnObjectPickerChanged(ComponentDescription componentDescription, WrappedVariable variable, UnityObjectDescription objectDescription)
        {
            Debug.Log("OnObjectPickerChanged");
            variable.Value = (objectDescription != null) ? objectDescription.InstanceID : 0;
            SendToPlayers(new SetVariableRequest(componentDescription.InstanceID, variable));

            //SendToPlayers(APIRequest.GetUnityObjects, componentDescription, variable.TypeFullName, variable.AssemblyName);
        }

        int SendToPlayers(BaseRequest request)
        {
            byte[] bytes;
            using (MemoryStream ms = new MemoryStream())
            {
                using (BinaryWriter bw = new BinaryWriter(ms))
                {
                    lastRequestID++;
                    bw.Write(lastRequestID);

                    bw.Write(request.GetType().Name);
                    request.Write(bw);
                }
                bytes = ms.ToArray();
            }
            if (settings.InspectionConnection == InspectionConnection.LocalEditor)
            {
                byte[] testResponse = SidekickRequestProcessor.Process(bytes);
                MessageEventArgs messageEvent = new MessageEventArgs();
                messageEvent.data = testResponse;
                OnMessageEvent(messageEvent);
            }
            else
            {
                EditorConnection.instance.Send(RuntimeSidekick.kMsgSendEditorToPlayer, bytes);
            }
            return lastRequestID;
        }


        void DoToolbar()
        {
            GUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Space(100);
            GUILayout.FlexibleSpace();
            treeView.searchString = hierarchySearchField.OnToolbarGUI(treeView.searchString);
            GUILayout.EndHorizontal();
        }

        void DoTreeView()
        {
            Rect rect = GUILayoutUtility.GetRect(200, 300, 300, 300);
            treeView.OnGUI(rect);
        }
    }
}