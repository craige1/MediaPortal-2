﻿using MediaPortal.Common;
using MediaPortal.Common.Logging;
using MediaPortal.Common.MediaManagement;
using MediaPortal.Plugins.MP2Extended.Attributes;
using MediaPortal.Plugins.MP2Extended.Common;
using MediaPortal.Plugins.MP2Extended.Exceptions;
using MediaPortal.Plugins.MP2Extended.Extensions;
using MediaPortal.Plugins.MP2Extended.MAS.TvShow;
using MediaPortal.Plugins.MP2Extended.ResourceAccess.MAS.TvShow.BaseClasses;
using System.Collections.Generic;
using System.Linq;

namespace MediaPortal.Plugins.MP2Extended.ResourceAccess.MAS.TvShow
{
  [ApiFunctionDescription(Type = ApiFunctionDescription.FunctionType.Json, Summary = "")]
  [ApiFunctionParam(Name = "sort", Type = typeof(WebSortField), Nullable = true)]
  [ApiFunctionParam(Name = "order", Type = typeof(WebSortOrder), Nullable = true)]
  [ApiFunctionParam(Name = "filter", Type = typeof(string), Nullable = true)]
  internal class GetTVShowsBasic : BaseTvShowBasic
  {
    public IList<WebTVShowBasic> Process(string filter, WebSortField? sort, WebSortOrder? order)
    {
      IList<MediaItem> items = GetMediaItems.GetMediaItemsByAspect(BasicNecessaryMIATypeIds, BasicOptionalMIATypeIds, null);

      if (items.Count == 0)
        throw new BadRequestException("GetTVShowsBasic: no Tv Episodes found");

      var output = items.Select(item => TVShowBasic(item))
        .Filter(filter);

      // sort and filter
      if (sort != null && order != null)
        output = output.SortWebTVShowBasic(sort, order).ToList();

      return output.ToList();
    }

    internal static ILogger Logger
    {
      get { return ServiceRegistration.Get<ILogger>(); }
    }
  }
}