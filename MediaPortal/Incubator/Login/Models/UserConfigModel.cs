#region Copyright (C) 2007-2017 Team MediaPortal

/*
    Copyright (C) 2007-2017 Team MediaPortal
    http://www.team-mediaportal.com

    This file is part of MediaPortal 2

    MediaPortal 2 is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    MediaPortal 2 is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with MediaPortal 2. If not, see <http://www.gnu.org/licenses/>.
*/

#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using MediaPortal.Common;
using MediaPortal.Common.Exceptions;
using MediaPortal.Common.General;
using MediaPortal.Common.Logging;
using MediaPortal.Common.MediaManagement;
using MediaPortal.Common.Messaging;
using MediaPortal.UI.Presentation.DataObjects;
using MediaPortal.UI.Presentation.Models;
using MediaPortal.UI.Presentation.Screens;
using MediaPortal.UI.Presentation.Workflow;
using MediaPortal.UI.ServerCommunication;
using MediaPortal.UI.Shares;
using MediaPortal.UiComponents.Login.General;
using MediaPortal.UI.Services.UserManagement;
using MediaPortal.Common.UserProfileDataManagement;
using MediaPortal.Common.SystemCommunication;
using MediaPortal.Common.Localization;
using System.IO;
using MediaPortal.Utilities.Graphics;
using System.Drawing.Imaging;

namespace MediaPortal.UiComponents.Login.Models
{
  /// <summary>
  /// Provides a workflow model to attend the complex configuration process for server and client shares
  /// in the MP2 configuration.
  /// </summary>
  public class UserConfigModel : IWorkflowModel, IDisposable
  {
    #region Consts

    public const string STR_MODEL_ID_USERCONFIG = "9B20B421-DF2E-42B6-AFF2-7EB6B60B601D";
    public static readonly Guid MODEL_ID_USERCONFIG = new Guid(STR_MODEL_ID_USERCONFIG);
    public static int MAX_IMAGE_WIDTH = 64;
    public static int MAX_IMAGE_HEIGHT = 64;

    #endregion

    #region Protected fields

    protected object _syncObj = new object();
    protected bool _updatingProperties = false;
    protected ItemsList _serverSharesList = null;
    protected ItemsList _localSharesList = null;
    protected ItemsList _userList = null;
    protected ItemsList _profileList = null;
    protected UserProxy _userProxy = null; // Encapsulates state and communication of user configuration
    protected AbstractProperty _isHomeServerConnectedProperty;
    protected AbstractProperty _showLocalSharesProperty;
    protected AbstractProperty _isLocalHomeServerProperty;
    protected AbstractProperty _anyShareAvailableProperty;
    protected AbstractProperty _selectShareInfoProperty;
    protected AbstractProperty _profileTypeNameProperty;
    protected AbstractProperty _isUserSelectedProperty;
    protected AsynchronousMessageQueue _messageQueue = null;

    #endregion

    #region Ctor

    public UserConfigModel()
    {
      _isHomeServerConnectedProperty = new WProperty(typeof(bool), false);
      _showLocalSharesProperty = new WProperty(typeof(bool), false);
      _isLocalHomeServerProperty = new WProperty(typeof(bool), false);
      _anyShareAvailableProperty = new WProperty(typeof(bool), false);
      _selectShareInfoProperty = new WProperty(typeof(string), string.Empty);
      _profileTypeNameProperty = new WProperty(typeof(string), string.Empty);
      _isUserSelectedProperty = new WProperty(typeof(bool), false);

      _profileList = new ItemsList();
      ListItem item = new ListItem();
      item.SetLabel(Consts.KEY_NAME, LocalizationHelper.Translate(Consts.RES_CLIENT_PROFILE_TEXT));
      item.AdditionalProperties[Consts.KEY_PROFILE_TYPE] = UserProfile.CLIENT_PROFILE;
      _profileList.Add(item);
      item = new ListItem();
      item.SetLabel(Consts.KEY_NAME, LocalizationHelper.Translate(Consts.RES_USER_PROFILE_TEXT));
      item.AdditionalProperties[Consts.KEY_PROFILE_TYPE] = UserProfile.USER_PROFILE;
      _profileList.Add(item);
      item = new ListItem();
      item.SetLabel(Consts.KEY_NAME, LocalizationHelper.Translate(Consts.RES_ADMIN_PROFILE_TEXT));
      item.AdditionalProperties[Consts.KEY_PROFILE_TYPE] = UserProfile.ADMIN_PROFILE;
      _profileList.Add(item);

      UserProxy = new UserProxy();
      UserProxy.ProfileTypeProperty.Attach(OnProfileTypeChanged);
      ProfileTypeName = ProfileTypeList.FirstOrDefault(i => (int)i.AdditionalProperties[Consts.KEY_PROFILE_TYPE] == UserProxy.ProfileType)?.Labels[Consts.KEY_NAME].Evaluate();

      UpdateUserLists_NoLock(true);
      UpdateShareLists_NoLock(true);
    }

    public void Dispose()
    {
      UserProxy = null;
      _serverSharesList = null;
      _localSharesList = null;
      _userList = null;
      _profileList = null;
    }

    #endregion

    void SubscribeToMessages()
    {
      AsynchronousMessageQueue messageQueue = new AsynchronousMessageQueue(this, new string[]
        {
           ServerConnectionMessaging.CHANNEL,
           ContentDirectoryMessaging.CHANNEL,
           SharesMessaging.CHANNEL,
        });
      messageQueue.MessageReceived += OnMessageReceived;
      messageQueue.Start();
      lock (_syncObj)
        _messageQueue = messageQueue;
    }

    void UnsubscribeFromMessages()
    {
      AsynchronousMessageQueue messageQueue;
      lock (_syncObj)
      {
        messageQueue = _messageQueue;
        _messageQueue = null;
      }
      if (messageQueue == null)
        return;
      messageQueue.Shutdown();
    }

    void OnMessageReceived(AsynchronousMessageQueue queue, SystemMessage message)
    {
      if (message.ChannelName == ServerConnectionMessaging.CHANNEL)
      {
        ServerConnectionMessaging.MessageType messageType =
            (ServerConnectionMessaging.MessageType)message.MessageType;
        switch (messageType)
        {
          case ServerConnectionMessaging.MessageType.HomeServerAttached:
          case ServerConnectionMessaging.MessageType.HomeServerDetached:
          case ServerConnectionMessaging.MessageType.HomeServerConnected:
            UpdateUserLists_NoLock(false);
            UpdateShareLists_NoLock(false);
            break;
          case ServerConnectionMessaging.MessageType.HomeServerDisconnected:
            UpdateUserLists_NoLock(false);
            UpdateShareLists_NoLock(false);
            break;
        }
      }
      else if (message.ChannelName == ContentDirectoryMessaging.CHANNEL)
      {
        ContentDirectoryMessaging.MessageType messageType = (ContentDirectoryMessaging.MessageType)message.MessageType;
        switch (messageType)
        {
          case ContentDirectoryMessaging.MessageType.RegisteredSharesChanged:
            UpdateShareLists_NoLock(false);
            break;
        }
      }
      else if (message.ChannelName == SharesMessaging.CHANNEL)
      {
        SharesMessaging.MessageType messageType = (SharesMessaging.MessageType)message.MessageType;
        switch (messageType)
        {
          case SharesMessaging.MessageType.ShareAdded:
          case SharesMessaging.MessageType.ShareRemoved:
            UpdateShareLists_NoLock(false);
            break;
        }
      }
    }

    #region Public properties (Also accessed from the GUI)

    public UserProxy UserProxy
    {
      get { return _userProxy; }
      private set
      {
        lock (_syncObj)
        {
          if (_userProxy != null)
            _userProxy.Dispose();
          _userProxy = value;
        }
      }
    }

    public ItemsList ServerSharesList
    {
      get
      {
        lock (_syncObj)
          return _serverSharesList;
      }
    }

    public ItemsList LocalSharesList
    {
      get
      {
        lock (_syncObj)
          return _localSharesList;
      }
    }

    public ItemsList UserList
    {
      get
      {
        lock (_syncObj)
          return _userList;
      }
    }

    public ItemsList ProfileTypeList
    {
      get
      {
        foreach (var item in _profileList)
        {
          if (UserProxy != null)
            item.Selected = (int)item.AdditionalProperties[Consts.KEY_PROFILE_TYPE] == UserProxy.ProfileType;
        }
        return _profileList;
      }
    }

    public AbstractProperty IsHomeServerConnectedProperty
    {
      get { return _isHomeServerConnectedProperty; }
    }

    public bool IsHomeServerConnected
    {
      get { return (bool)_isHomeServerConnectedProperty.GetValue(); }
      set { _isHomeServerConnectedProperty.SetValue(value); }
    }

    public AbstractProperty IsLocalHomeServerProperty
    {
      get { return _isLocalHomeServerProperty; }
    }

    public bool IsLocalHomeServer
    {
      get { return (bool)_isLocalHomeServerProperty.GetValue(); }
      set { _isLocalHomeServerProperty.SetValue(value); }
    }

    public AbstractProperty ShowLocalSharesProperty
    {
      get { return _showLocalSharesProperty; }
    }

    public bool ShowLocalShares
    {
      get { return (bool)_showLocalSharesProperty.GetValue(); }
      set { _showLocalSharesProperty.SetValue(value); }
    }

    public AbstractProperty AnyShareAvailableProperty
    {
      get { return _anyShareAvailableProperty; }
    }

    public bool AnyShareAvailable
    {
      get { return (bool)_anyShareAvailableProperty.GetValue(); }
      set { _anyShareAvailableProperty.SetValue(value); }
    }

    public AbstractProperty SelectedSharesInfoProperty
    {
      get { return _selectShareInfoProperty; }
    }

    public string SelectedSharesInfo
    {
      get { return (string)_selectShareInfoProperty.GetValue(); }
      set { _selectShareInfoProperty.SetValue(value); }
    }

    public AbstractProperty ProfileTypeNameProperty
    {
      get { return _profileTypeNameProperty; }
    }

    public string ProfileTypeName
    {
      get { return (string)_profileTypeNameProperty.GetValue(); }
      set { _profileTypeNameProperty.SetValue(value); }
    }

    public AbstractProperty IsUserSelectedProperty
    {
      get { return _isUserSelectedProperty; }
    }

    public bool IsUserSelected
    {
      get { return (bool)_isUserSelectedProperty.GetValue(); }
      set { _isUserSelectedProperty.SetValue(value); }
    }

    public string ImagePath
    {
      get { return ""; }
      set
      {
        if(File.Exists(value))
        {
          using(FileStream stream = new FileStream(value, FileMode.Open))
          using (MemoryStream resized = (MemoryStream)ImageUtilities.ResizeImage(stream, ImageFormat.Jpeg, MAX_IMAGE_WIDTH, MAX_IMAGE_HEIGHT))
          {
            if (resized != null)
              UserProxy.Image = resized.ToArray();
          }
        }
      }
    }

    #endregion

    #region Public methods

    public void OpenChooseProfileTypeDialog()
    {
      ServiceRegistration.Get<IScreenManager>().ShowDialog("DialogChooseProfileType");
    }

    public void OpenSelectSharesDialog()
    {
      ServiceRegistration.Get<IScreenManager>().ShowDialog("DialogSelectShares",
        (string name, System.Guid id) =>
        {
          UserProxy.SelectedShares.Clear();
          foreach (ListItem item in _serverSharesList.Where(i => i.Selected))
            UserProxy.SelectedShares.Add(((Share)item.AdditionalProperties[Consts.KEY_SHARE]).ShareId);
          foreach (ListItem item in _localSharesList.Where(i => i.Selected))
            UserProxy.SelectedShares.Add(((Share)item.AdditionalProperties[Consts.KEY_SHARE]).ShareId);
          SetSelectedShares();
        });
    }

    public void AddUser()
    {
      try
      {
        UserProfile user = new UserProfile(Guid.Empty, LocalizationHelper.Translate(Consts.RES_NEW_USER_TEXT), UserProfile.USER_PROFILE);

        ListItem item = new ListItem();
        item.SetLabel(Consts.KEY_NAME, user.Name);
        item.AdditionalProperties[Consts.KEY_USER] = user;
        item.SelectedProperty.Attach(OnUserItemSelectionChanged);
        item.Selected = true;

        lock (_syncObj)
          _userList.Add(item);

        SetUser(user);

        _userList.FireChange();
      }
      catch (Exception e)
      {
        ServiceRegistration.Get<ILogger>().Error("UserConfigModel: Problems adding user", e);
      }
    }

    public void DeleteUser()
    {
      try
      {
        ListItem item = _userList.FirstOrDefault(i => i.Selected);
        if (item == null)
          return;

        UserProfile user = (UserProfile)item.AdditionalProperties[Consts.KEY_USER];

        lock (_syncObj)
          _userList.Remove(item);

        if (user.ProfileId != Guid.Empty)
        {
          IUserManagement userManagement = ServiceRegistration.Get<IUserManagement>();
          if (userManagement != null && userManagement.UserProfileDataManagement != null)
          {
            if (!userManagement.UserProfileDataManagement.DeleteProfile(user.ProfileId))
            {
              ServiceRegistration.Get<ILogger>().Warn("UserConfigModel: Problems deleting user '{0}' (name '{1}')", user.ProfileId, user.Name);
            }
          }
        }

        _userList.FireChange();
      }
      catch (NotConnectedException)
      {
        DisconnectedError();
      }
      catch (Exception e)
      {
        ServiceRegistration.Get<ILogger>().Error("UserConfigModel: Problems deleting user", e);
      }
    }

    public void SaveUser()
    {
      try
      {
        if (UserProxy.IsUserValid)
        {
          int shareCount = 0;
          bool success = true;
          IUserManagement userManagement = ServiceRegistration.Get<IUserManagement>();
          if (userManagement != null && userManagement.UserProfileDataManagement != null)
          {
            string hash = Utils.HashPassword(UserProxy.Password);
            if (UserProxy.Id == Guid.Empty)
            {
              UserProxy.Id = userManagement.UserProfileDataManagement.CreateProfile(UserProxy.UserName, UserProxy.ProfileType, hash);
            }
            else
            {
              success = userManagement.UserProfileDataManagement.UpdateProfile(UserProxy.Id, UserProxy.UserName, UserProxy.ProfileType, hash);
            }
            if (UserProxy.Id == Guid.Empty)
            {
              ServiceRegistration.Get<ILogger>().Error("UserConfigModel: Problems saving user '{0}'", UserProxy.UserName);
              return;
            }

            if(UserProxy.Image != null)
              success &= userManagement.UserProfileDataManagement.SetProfileImage(UserProxy.Id, UserProxy.Image);
            success &= userManagement.UserProfileDataManagement.SetUserAdditionalData(UserProxy.Id, UserDataKeysKnown.KEY_ALLOWED_AGE, UserProxy.AllowedAge.ToString());
            success &= userManagement.UserProfileDataManagement.ClearUserAdditionalDataKey(UserProxy.Id, UserDataKeysKnown.KEY_ALLOWED_SHARE);
            foreach (var shareId in UserProxy.SelectedShares)
              success &= userManagement.UserProfileDataManagement.SetUserAdditionalData(UserProxy.Id, UserDataKeysKnown.KEY_ALLOWED_SHARE, ++shareCount, shareId.ToString());
            success &= userManagement.UserProfileDataManagement.SetUserAdditionalData(UserProxy.Id, UserDataKeysKnown.KEY_ALLOW_ALL_AGES, UserProxy.AllowAllAges ? "1" : "0");
            success &= userManagement.UserProfileDataManagement.SetUserAdditionalData(UserProxy.Id, UserDataKeysKnown.KEY_ALLOW_ALL_SHARES, UserProxy.AllowAllShares ? "1" : "0");
            success &= userManagement.UserProfileDataManagement.SetUserAdditionalData(UserProxy.Id, UserDataKeysKnown.KEY_INCLUDE_PARENT_GUIDED_CONTENT, UserProxy.IncludeParentGuidedContent ? "1" : "0");

            if (!success)
            {
              ServiceRegistration.Get<ILogger>().Error("UserConfigModel: Problems saving setup for user '{0}'", UserProxy.UserName);
              return;
            }
          }

          ListItem item = _userList.FirstOrDefault(i => i.Selected);
          if (item == null)
            return;

          shareCount = 0;
          UserProfile user = new UserProfile(UserProxy.Id, UserProxy.UserName, UserProxy.ProfileType, UserProxy.Password);
          user.AddAdditionalData(UserDataKeysKnown.KEY_ALLOWED_AGE, UserProxy.AllowedAge.ToString());
          foreach (var shareId in UserProxy.SelectedShares)
            user.AddAdditionalData(UserDataKeysKnown.KEY_ALLOWED_SHARE, ++shareCount, shareId.ToString());
          user.AddAdditionalData(UserDataKeysKnown.KEY_ALLOW_ALL_AGES, UserProxy.AllowAllAges ? "1" : "0");
          user.AddAdditionalData(UserDataKeysKnown.KEY_ALLOW_ALL_SHARES, UserProxy.AllowAllShares ? "1" : "0");
          user.AddAdditionalData(UserDataKeysKnown.KEY_INCLUDE_PARENT_GUIDED_CONTENT, UserProxy.IncludeParentGuidedContent ? "1" : "0");

          item.SetLabel(Consts.KEY_NAME, user.Name);
          item.AdditionalProperties[Consts.KEY_USER] = user;
          _userList.FireChange();

          SetUser(user);
        }
      }
      catch (NotConnectedException)
      {
        DisconnectedError();
      }
      catch (Exception e)
      {
        ServiceRegistration.Get<ILogger>().Error("UserConfigModel: Problems saving user", e);
      }
    }

    public void SelectProfileType(ListItem item)
    {
      int profileType = (int)item.AdditionalProperties[Consts.KEY_PROFILE_TYPE];
      UserProxy.ProfileType = profileType;
    }

    #endregion

    #region Private and protected methods

    private void SetUser(UserProfile userProfile)
    {
      try
      {
        if (userProfile != null && UserProxy != null)
        {
          UserProxy.Id = userProfile.ProfileId;
          UserProxy.UserName = userProfile.Name;
          UserProxy.Password = userProfile.Password;
          UserProxy.ProfileType = userProfile.ProfileType;
          UserProxy.LastLogin = userProfile.LastLogin ?? DateTime.MinValue;

          UserProxy.SelectedShares.Clear();
          int allowedAge = 5;
          bool allowAllAges = true;
          bool allowAllShares = true;
          bool includeParentContent = false;
          string preferredMovieCountry = string.Empty;
          string preferredSeriesCountry = string.Empty;

          foreach (var data in userProfile.AdditionalData)
          {
            foreach (var val in data.Value)
            {
              if (data.Key == UserDataKeysKnown.KEY_ALLOWED_AGE)
                allowedAge = Convert.ToInt32(val.Value);
              else if (data.Key == UserDataKeysKnown.KEY_ALLOW_ALL_AGES)
                allowAllAges = Convert.ToInt32(val.Value) > 0;
              else if (data.Key == UserDataKeysKnown.KEY_ALLOW_ALL_SHARES)
                allowAllShares = Convert.ToInt32(val.Value) > 0;
              else if (data.Key == UserDataKeysKnown.KEY_ALLOWED_SHARE)
              {
                Guid shareId = Guid.Parse(val.Value);
                if (_localSharesList.Where(i => ((Share)i.AdditionalProperties[Consts.KEY_SHARE]).ShareId == shareId).Any() ||
                  _serverSharesList.Where(i => ((Share)i.AdditionalProperties[Consts.KEY_SHARE]).ShareId == shareId).Any())
                  UserProxy.SelectedShares.Add(shareId);
              }
              else if (data.Key == UserDataKeysKnown.KEY_INCLUDE_PARENT_GUIDED_CONTENT)
                includeParentContent = Convert.ToInt32(val.Value) > 0;
            }
          }

          UserProxy.AllowAllAges = allowAllAges;
          UserProxy.AllowAllShares = allowAllShares;
          UserProxy.AllowedAge = allowedAge;
          UserProxy.IncludeParentGuidedContent = includeParentContent;
        }
        else if (UserProxy != null)
        {
          UserProxy.Id = Guid.Empty;
          UserProxy.UserName = String.Empty;
          UserProxy.Password = String.Empty;
          UserProxy.ProfileType = UserProfile.USER_PROFILE;
          UserProxy.LastLogin = DateTime.MinValue;

          UserProxy.SelectedShares.Clear();

          UserProxy.AllowAllAges = true;
          UserProxy.AllowAllShares = true;
          UserProxy.AllowedAge = 5;
          UserProxy.IncludeParentGuidedContent = false;
        }

        SetSelectedShares();
      }
      catch (Exception e)
      {
        ServiceRegistration.Get<ILogger>().Error("UserConfigModel: Error selecting user", e);
      }
    }

    private void SetSelectedShares()
    {
      if (UserProxy != null)
        SelectedSharesInfo = string.Format("{0}: {1}", LocalizationHelper.Translate(Consts.RES_SHARES_TEXT), UserProxy.SelectedShares.Count);
    }

    protected internal void UpdateUserLists_NoLock(bool create)
    {
      lock (_syncObj)
      {
        if (_updatingProperties)
          return;
        _updatingProperties = true;
        if (create)
          _userList = new ItemsList();
      }
      try
      {
        IUserManagement userManagement = ServiceRegistration.Get<IUserManagement>();
        if (userManagement == null || userManagement.UserProfileDataManagement == null)
          return;

        // add users to expose them
        var users = userManagement.UserProfileDataManagement.GetProfiles();
        _userList.Clear();
        foreach (UserProfile user in users)
        {
          ListItem item = new ListItem();
          item.SetLabel(Consts.KEY_NAME, user.Name);
          item.AdditionalProperties[Consts.KEY_USER] = user;
          item.SelectedProperty.Attach(OnUserItemSelectionChanged);
          lock (_syncObj)
            _userList.Add(item);
        }
      }
      catch (NotConnectedException)
      {
        throw;
      }
      catch (Exception e)
      {
        ServiceRegistration.Get<ILogger>().Warn("Problems updating users", e);
      }
      finally
      {
        lock (_syncObj)
          _updatingProperties = false;
      }
    }

    private void OnUserItemSelectionChanged(AbstractProperty property, object oldValue)
    {
      UserProfile userProfile = null;
      lock (_syncObj)
      {
        userProfile = _userList.Where(i => i.Selected).Select(i => (UserProfile)i.AdditionalProperties[Consts.KEY_USER]).FirstOrDefault();
      }
      SetUser(userProfile);
      IsUserSelected = userProfile != null;
    }

    private void OnProfileTypeChanged(AbstractProperty property, object oldValue)
    {
      ProfileTypeName = ProfileTypeList.FirstOrDefault(i => (int)i.AdditionalProperties[Consts.KEY_PROFILE_TYPE] == UserProxy.ProfileType)?.Labels[Consts.KEY_NAME].Evaluate();
    }

    protected internal void UpdateShareLists_NoLock(bool create)
    {
      lock (_syncObj)
      {
        if (_updatingProperties)
          return;
        _updatingProperties = true;
        if (create)
        {
          _serverSharesList = new ItemsList();
          _localSharesList = new ItemsList();
        }
      }
      try
      {
        ILocalSharesManagement sharesManagement = ServiceRegistration.Get<ILocalSharesManagement>();
        var shares = sharesManagement.Shares.Values;
        _localSharesList.Clear();
        foreach (Share share in shares)
        {
          ListItem item = new ListItem();
          item.SetLabel(Consts.KEY_NAME, share.Name);
          item.AdditionalProperties[Consts.KEY_SHARE] = share;
          if (UserProxy != null)
            item.Selected = UserProxy.SelectedShares.Contains(share.ShareId);
          lock (_syncObj)
            _localSharesList.Add(item);
        }

        IServerConnectionManager scm = ServiceRegistration.Get<IServerConnectionManager>();
        if (scm == null || scm.ContentDirectory == null)
          return;

        // add users to expose them
        shares = scm.ContentDirectory.GetShares(scm.HomeServerSystemId, SharesFilter.All);
        _serverSharesList.Clear();
        foreach (Share share in shares)
        {
          ListItem item = new ListItem();
          item.SetLabel(Consts.KEY_NAME, share.Name);
          item.AdditionalProperties[Consts.KEY_SHARE] = share;
          if (UserProxy != null)
            item.Selected = UserProxy.SelectedShares.Contains(share.ShareId);
          lock (_syncObj)
            _serverSharesList.Add(item);
        }
        IsHomeServerConnected = scm.LastHomeServerSystem != null;
        ShowLocalShares = !IsLocalHomeServer || _localSharesList.Count > 0;
        AnyShareAvailable = _serverSharesList.Count > 0 || _localSharesList.Count > 0;
      }
      catch (NotConnectedException)
      {
        throw;
      }
      catch (Exception e)
      {
        ServiceRegistration.Get<ILogger>().Error("Problems updating shares", e);
      }
      finally
      {
        lock (_syncObj)
          _updatingProperties = false;
      }
    }

    protected void ClearData()
    {
      lock (_syncObj)
      {
        //UserProxy = null;
        _userList = null;
        _localSharesList = null;
        _serverSharesList = null;
        SetUser(null);
      }
    }

    protected void DisconnectedError()
    {
      // Called when a remote call crashes because the server was disconnected. We don't do anything here because
      // we automatically move to the overview state in the OnMessageReceived method when the server disconnects.
    }

    #endregion

    #region IWorkflowModel implementation

    public Guid ModelId
    {
      get { return MODEL_ID_USERCONFIG; }
    }

    public bool CanEnterState(NavigationContext oldContext, NavigationContext newContext)
    {
      return true;
    }

    public void EnterModelContext(NavigationContext oldContext, NavigationContext newContext)
    {
      SubscribeToMessages();
      ClearData();
      UpdateUserLists_NoLock(true);
      UpdateShareLists_NoLock(true);
    }

    public void ExitModelContext(NavigationContext oldContext, NavigationContext newContext)
    {
      UnsubscribeFromMessages();
      ClearData();
    }

    public void ChangeModelContext(NavigationContext oldContext, NavigationContext newContext, bool push)
    {

    }

    public void Deactivate(NavigationContext oldContext, NavigationContext newContext)
    {
      // Nothing to do here
    }

    public void Reactivate(NavigationContext oldContext, NavigationContext newContext)
    {

    }

    public void UpdateMenuActions(NavigationContext context, IDictionary<Guid, WorkflowAction> actions)
    {
      // Perhaps we'll add menu actions later for different convenience procedures.
    }

    public ScreenUpdateMode UpdateScreen(NavigationContext context, ref string screen)
    {
      return ScreenUpdateMode.AutoWorkflowManager;
    }

    #endregion
  }
}