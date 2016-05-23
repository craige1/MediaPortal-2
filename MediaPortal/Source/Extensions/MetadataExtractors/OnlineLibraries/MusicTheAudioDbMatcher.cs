#region Copyright (C) 2007-2015 Team MediaPortal

/*
    Copyright (C) 2007-2015 Team MediaPortal
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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MediaPortal.Common;
using MediaPortal.Common.Localization;
using MediaPortal.Common.Logging;
using MediaPortal.Common.MediaManagement.Helpers;
using MediaPortal.Common.PathManager;
using MediaPortal.Extensions.OnlineLibraries.Libraries.AudioDbV1.Data;
using MediaPortal.Extensions.OnlineLibraries.Matches;
using MediaPortal.Extensions.OnlineLibraries.TheAudioDB;
using System.Linq;
using MediaPortal.Common.MediaManagement.DefaultItemAspects;

namespace MediaPortal.Extensions.OnlineLibraries
{
  public class MusicTheAudioDbMatcher : BaseMatcher<TrackMatch, string>
  {
    #region Static instance

    public static MusicTheAudioDbMatcher Instance
    {
      get { return ServiceRegistration.Get<MusicTheAudioDbMatcher>(); }
    }

    #endregion

    #region Constants

    public static string CACHE_PATH = ServiceRegistration.Get<IPathManager>().GetPath(@"<DATA>\TheAudioDB\");
    protected static string _matchesSettingsFile = Path.Combine(CACHE_PATH, "Matches.xml");
    protected static TimeSpan MAX_MEMCACHE_DURATION = TimeSpan.FromHours(12);

    protected override string MatchesSettingsFile
    {
      get { return _matchesSettingsFile; }
    }

    #endregion

    #region Fields

    protected DateTime _memoryCacheInvalidated = DateTime.MinValue;
    protected ConcurrentDictionary<string, AudioDbTrack> _memoryCache = new ConcurrentDictionary<string, AudioDbTrack>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Contains the initialized TheAudioDbWrapper.
    /// </summary>
    private TheAudioDbWrapper _audioDb;

    #endregion

    #region Metadata updaters

    public bool FindAndUpdateTrack(TrackInfo trackInfo)
    {
      try
      {
        AudioDbTrack trackDetails;
        if (TryMatch(trackInfo, out trackDetails))
        {
          string id = null;
          if (trackDetails != null)
          {
            id = trackDetails.TrackId.ToString();

            MetadataUpdater.SetOrUpdateId(ref trackInfo.AudioDbId, trackDetails.TrackId);
            MetadataUpdater.SetOrUpdateId(ref trackInfo.MusicBrainzId, trackDetails.MusicBrainzID);
            MetadataUpdater.SetOrUpdateId(ref trackInfo.AlbumAudioDbId, trackDetails.AlbumId.HasValue ? trackDetails.AlbumId.Value : 0);
            MetadataUpdater.SetOrUpdateId(ref trackInfo.AlbumMusicBrainzGroupId, trackDetails.MusicBrainzAlbumID);

            MetadataUpdater.SetOrUpdateString(ref trackInfo.TrackName, trackDetails.Track, true);
            MetadataUpdater.SetOrUpdateString(ref trackInfo.Album, trackDetails.Album, true);
            MetadataUpdater.SetOrUpdateValue(ref trackInfo.TrackNum, trackDetails.TrackNumber);
            MetadataUpdater.SetOrUpdateValue(ref trackInfo.DiscNum, trackDetails.CD.HasValue ? trackDetails.CD.Value : 0);
            MetadataUpdater.SetOrUpdateRatings(ref trackInfo.TotalRating, ref trackInfo.RatingCount, trackDetails.Rating, trackDetails.RatingCount);
            MetadataUpdater.SetOrUpdateString(ref trackInfo.TrackLyrics, trackDetails.TrackLyrics, false);

            if (trackDetails.ArtistId.HasValue)
            {
              MetadataUpdater.SetOrUpdateList(trackInfo.Artists, ConvertToPersons(trackDetails.ArtistId.Value, trackDetails.MusicBrainzArtistID,
                trackDetails.Artist, PersonAspect.OCCUPATION_ARTIST), true, false);
              MetadataUpdater.SetOrUpdateList(trackInfo.AlbumArtists, ConvertToPersons(trackDetails.ArtistId.Value, trackDetails.MusicBrainzArtistID,
                trackDetails.Artist, PersonAspect.OCCUPATION_ARTIST), true, false);
            }

            MetadataUpdater.SetOrUpdateList(trackInfo.Genres, new List<string>(new string[] { trackDetails.Genre }), true, true);

            AudioDbAlbum album;
            if (trackDetails.AlbumId.HasValue && _audioDb.GetAlbumFromId(trackDetails.AlbumId.Value, out album))
            {
              if (album.LabelId.HasValue)
                MetadataUpdater.SetOrUpdateList(trackInfo.MusicLabels, ConvertToCompanies(album.LabelId.Value, album.Label, CompanyAspect.COMPANY_MUSIC_LABEL), true, false);

              if (trackInfo.Thumbnail == null)
              {
                List<string> thumbs = GetFanArtFiles(trackInfo, FanArtScope.Album, FanArtType.Covers);
                if (thumbs.Count > 0)
                  trackInfo.Thumbnail = File.ReadAllBytes(thumbs[0]);
              }
            }
          }

          if (!string.IsNullOrEmpty(id))
            ScheduleDownload(id);
          return true;
        }
        return false;
      }
      catch (Exception ex)
      {
        ServiceRegistration.Get<ILogger>().Debug("MusicTheAudioDbMatcher: Exception while processing track {0}", ex, trackInfo.ToString());
        return false;
      }
    }

    public bool UpdateAlbum(AlbumInfo albumInfo)
    {
      try
      {
        AudioDbAlbum albumDetails;
        if (albumInfo.AudioDbId > 0 && _audioDb.GetAlbumFromId(albumInfo.AudioDbId, out albumDetails))
        {
          MetadataUpdater.SetOrUpdateId(ref albumInfo.MusicBrainzGroupId, albumDetails.MusicBrainzID);

          MetadataUpdater.SetOrUpdateString(ref albumInfo.Album, albumDetails.Album, false);
          MetadataUpdater.SetOrUpdateString(ref albumInfo.Description, albumDetails.Description, false);

          MetadataUpdater.SetOrUpdateList(albumInfo.Genres, new List<string>(new string[] { albumDetails.Genre }), true, true);
          MetadataUpdater.SetOrUpdateValue(ref albumInfo.Sales, albumDetails.Sales.HasValue ? albumDetails.Sales.Value : 0);
          MetadataUpdater.SetOrUpdateRatings(ref albumInfo.TotalRating, ref albumInfo.RatingCount, albumDetails.Rating, albumDetails.RatingCount);
          if (albumDetails.Year.HasValue) MetadataUpdater.SetOrUpdateValue(ref albumInfo.ReleaseDate, new DateTime(albumDetails.Year.Value, 1, 1));

          if (albumDetails.ArtistId.HasValue)
          {
            MetadataUpdater.SetOrUpdateList(albumInfo.Artists, ConvertToPersons(albumDetails.ArtistId.Value, albumDetails.MusicBrainzArtistID,
                  albumDetails.Artist, PersonAspect.OCCUPATION_ARTIST), true, false);
          }

          if (albumDetails.LabelId.HasValue)
            MetadataUpdater.SetOrUpdateList(albumInfo.MusicLabels, ConvertToCompanies(albumInfo.AudioDbId, albumDetails.Label, CompanyAspect.COMPANY_MUSIC_LABEL), true, false);

          if (albumInfo.Thumbnail == null)
          {
            List<string> thumbs = GetFanArtFiles(albumInfo, FanArtScope.Album, FanArtType.Covers);
            if (thumbs.Count > 0)
              albumInfo.Thumbnail = File.ReadAllBytes(thumbs[0]);
          }

          return true;
        }
        return false;
      }
      catch (Exception ex)
      {
        ServiceRegistration.Get<ILogger>().Debug("MusicTheAudioDbMatcher: Exception while processing album {0}", ex, albumInfo.ToString());
        return false;
      }
    }

    public bool UpdateAlbumPersons(AlbumInfo albumInfoInfo, string occupation)
    {
      return UpdatePersons(albumInfoInfo.Artists, occupation);
    }

    public bool UpdateTrackPersons(TrackInfo trackInfo, string occupation)
    {
      return UpdatePersons(trackInfo.Artists, occupation);
    }

    private bool UpdatePersons(List<PersonInfo> persons, string occupation)
    {
      try
      {
        if (occupation != PersonAspect.OCCUPATION_ARTIST)
          return false;

        // Try online lookup
        if (!Init())
          return false;

        AudioDbArtist artistDetails;
        int sortOrder = 0;
        foreach (PersonInfo person in persons)
        {
          if(person.AudioDbId <= 0)
          {
            List<AudioDbArtist> artists;
            if (_audioDb.SearchArtist(person.Name, out artists))
              person.AudioDbId = artists[0].ArtistId;
          }
          if (person.AudioDbId > 0 && _audioDb.GetArtistFromId(person.AudioDbId, out artistDetails))
          {
            int? year = artistDetails.BornYear == null ? artistDetails.FormedYear : artistDetails.BornYear;
            DateTime? born = null;
            if (year.HasValue) born = new DateTime(year.Value, 1, 1);
            DateTime? died = null;
            if (artistDetails.DiedYear.HasValue) died = new DateTime(artistDetails.DiedYear.Value, 1, 1);

            person.MusicBrainzId = artistDetails.MusicBrainzID;
            person.Name = artistDetails.Artist;
            person.Biography = artistDetails.Biography;
            person.DateOfBirth = born;
            person.DateOfDeath = died;
            person.Orign = artistDetails.Country;
            person.IsGroup = artistDetails.Members.HasValue ? artistDetails.Members.Value > 1 : false;
            person.Occupation = occupation;
            person.Order = sortOrder++;

            // Get Thumbnail
            if (person.Thumbnail == null)
            {
              List<string> thumbs = GetFanArtFiles(person, FanArtScope.Artist, FanArtType.Thumbnails);
              if (thumbs.Count > 0)
                person.Thumbnail = File.ReadAllBytes(thumbs[0]);
            }
          }
        }
        return true;
      }
      catch (Exception ex)
      {
        ServiceRegistration.Get<ILogger>().Debug("MusicTheAudioDbMatcher: Exception while processing persons", ex);
        return false;
      }
    }

    #endregion

    #region Metadata update helpers

    private List<PersonInfo> ConvertToPersons(long artistId, string mbArtistId, string artist, string occupation)
    {
      if (artistId == 0 || string.IsNullOrEmpty(artist))
        return new List<PersonInfo>();

      List<PersonInfo> retValue = new List<PersonInfo>();
      int sortOrder = 0;
      if (artistId > 0)
      {
        retValue.Add(
        new PersonInfo()
        {
          AudioDbId = artistId,
          MusicBrainzId = mbArtistId,
          Name = artist,
          Occupation = occupation,
          Order = sortOrder++
        });

      }
      return retValue;
    }

    private List<CompanyInfo> ConvertToCompanies(long companyId, string company, string type)
    {
      if (companyId == 0 || string.IsNullOrEmpty(company))
        return new List<CompanyInfo>();

      return new List<CompanyInfo>
      {
        new CompanyInfo()
        {
          AudioDbId = companyId,
          Name = company,
          Type = type,
          Order = 0
        }
      };
    }

    private void StoreTrackMatch(AudioDbTrack track, TrackInfo searchTrack)
    {
      if (track == null)
      {
        _storage.TryAddMatch(new TrackMatch()
        {
          ItemName = searchTrack.ToString()
        });
        return;
      }
      TrackInfo trackMatch = new TrackInfo()
      {
        Album = track.Album,
        TrackNum = track.TrackNumber,
        TrackName = track.Track
      };
      var onlineMatch = new TrackMatch
      {
        Id = track.TrackId.ToString(),
        ItemName = searchTrack.ToString(),
        TrackName = trackMatch.ToString(),
        TrackNum = track.TrackNumber,
        ArtistName = track.Artist,
        AlbumName = track.Album
      };
      _storage.TryAddMatch(onlineMatch);
    }

    #endregion

    #region Online matchers

    protected bool TryMatch(TrackInfo trackInfo, out AudioDbTrack trackDetails)
    {
      if ((trackInfo.AudioDbId > 0 && _audioDb.GetTrackFromId(trackInfo.AudioDbId, out trackDetails)) ||
        (!string.IsNullOrEmpty(trackInfo.MusicBrainzId) && _audioDb.GetTrackFromMBId(trackInfo.MusicBrainzId, out trackDetails)))
      {
        StoreTrackMatch(trackDetails, trackInfo);
        return true;
      }
      trackDetails = null;
      return TryMatch(trackInfo.TrackName, trackInfo.Artists.Select(a => a.Name).ToList(), trackInfo.Album, trackInfo.TrackNum, false, out trackDetails);
    }

    protected bool TryMatch(string title, List<string> artists, string album, int trackNum, bool cacheOnly, out AudioDbTrack trackDetail)
    {
      TrackInfo searchTrack = new TrackInfo()
      {
        Album = album,
        TrackNum = trackNum,
        TrackName = title
      };
      trackDetail = null;
      try
      {
        // Prefer memory cache
        CheckCacheAndRefresh();
        if (_memoryCache.TryGetValue(searchTrack.ToString(), out trackDetail))
          return true;

        // Load cache or create new list
        List<TrackMatch> matches = _storage.GetMatches();

        // Init empty
        trackDetail = null;

        // Use cached values before doing online query
        TrackMatch match = matches.Find(m =>
          (string.Equals(m.ItemName, searchTrack.ToString(), StringComparison.OrdinalIgnoreCase) || string.Equals(m.TrackName, searchTrack.ToString(), StringComparison.OrdinalIgnoreCase)) &&
          !string.IsNullOrEmpty(m.AlbumName) && album.Length > 0 ? album.Equals(m.AlbumName, StringComparison.OrdinalIgnoreCase) : true &&
          ((trackNum > 0 && m.TrackNum > 0 && int.Equals(m.TrackNum, trackNum) || trackNum <= 0 || m.TrackNum <= 0)));
        ServiceRegistration.Get<ILogger>().Debug("TheAudioDbMatcher: Try to lookup track \"{0}\" from cache: {1}", title, match != null && string.IsNullOrEmpty(match.Id) == false);

        // Try online lookup
        if (!Init())
          return false;

        // If this is a known movie, only return the track details.
        if (match != null)
          return !string.IsNullOrEmpty(match.Id) && _audioDb.GetTrackFromId(Convert.ToInt64(match.Id), out trackDetail);

        if (cacheOnly)
          return false;

        List<AudioDbTrack> tracks;
        if (_audioDb.SearchTrackUnique(title, artists, album, trackNum, out tracks))
        {
          AudioDbTrack trackResult = tracks[0];
          ServiceRegistration.Get<ILogger>().Debug("TheAudioDbMatcher: Found unique online match for \"{0}\": \"{1}\"", title, trackResult.Track);
          if (_audioDb.GetTrackFromId(trackResult.TrackId, out trackDetail))
          {
            StoreTrackMatch(trackDetail, searchTrack);
            return true;
          }
        }
        ServiceRegistration.Get<ILogger>().Debug("TheAudioDbMatcher: No unique match found for \"{0}\"", title);
        // Also save "non matches" to avoid retrying
        StoreTrackMatch(null, searchTrack);
        return false;
      }
      catch (Exception ex)
      {
        ServiceRegistration.Get<ILogger>().Debug("TheAudioDbMatcher: Exception while processing track {0}", ex, title);
        return false;
      }
      finally
      {
        if (trackDetail != null)
          _memoryCache.TryAdd(searchTrack.ToString(), trackDetail);
      }
    }

    #endregion

    #region Caching

    /// <summary>
    /// Check if the memory cache should be cleared and starts an online update of (file-) cached series information.
    /// </summary>
    private void CheckCacheAndRefresh()
    {
      if (DateTime.Now - _memoryCacheInvalidated <= MAX_MEMCACHE_DURATION)
        return;
      _memoryCache.Clear();
      _memoryCacheInvalidated = DateTime.Now;

      // TODO: when updating movie information is implemented, start here a job to do it
    }

    #endregion

    public override bool Init()
    {
      if (!base.Init())
        return false;

      if (_audioDb != null)
        return true;

      _audioDb = new TheAudioDbWrapper();
      // Try to lookup online content in the configured language
      CultureInfo currentCulture = ServiceRegistration.Get<ILocalization>().CurrentCulture;
      _audioDb.SetPreferredLanguage(currentCulture.TwoLetterISOLanguageName);
      return _audioDb.Init(CACHE_PATH);
    }

    #region FanArt

    public List<string> GetFanArtFiles<T>(T infoObject, string scope, string type)
    {
      List<string> fanartFiles = new List<string>();
      string path = null;
      if (scope == FanArtScope.Album)
      {
        AlbumInfo album = infoObject as AlbumInfo;
        TrackInfo track = infoObject as TrackInfo;
        if (album != null && album.AudioDbId > 0)
        {
          path = Path.Combine(CACHE_PATH, album.AudioDbId.ToString(), string.Format(@"{0}\{1}\", scope, type));
        }
        else if (track != null && track.AlbumAudioDbId > 0)
        {
          path = Path.Combine(CACHE_PATH, track.AlbumAudioDbId.ToString(), string.Format(@"{0}\{1}\", scope, type));
        }
      }
      else if (scope == FanArtScope.Artist)
      {
        PersonInfo person = infoObject as PersonInfo;
        if (person != null && person.AudioDbId > 0)
        {
          path = Path.Combine(CACHE_PATH, person.AudioDbId.ToString(), string.Format(@"{0}\{1}\", scope, type));
        }
      }
      if (Directory.Exists(path))
        fanartFiles.AddRange(Directory.GetFiles(path, "*.jpg"));
      return fanartFiles;
    }

    protected override void DownloadFanArt(string albumId)
    {
      try
      {
        if (string.IsNullOrEmpty(albumId))
          return;

        ServiceRegistration.Get<ILogger>().Debug("TheAudioDbMatcher Download: Started for ID {0}", albumId);

        if (!Init())
          return;

        int taadbId = 0;
        if (!int.TryParse(albumId, out taadbId))
          return;

        if (taadbId <= 0)
          return;

        AudioDbTrack track;
        if (!_audioDb.GetTrackFromId(taadbId, out track))
          return;

        if (track.AlbumId.HasValue)
        {
          AudioDbAlbum album;
          if (!_audioDb.GetAlbumFromId(track.AlbumId.Value, out album))
            return;

          // Save Cover
          ServiceRegistration.Get<ILogger>().Debug("TheAudioDbMatcher Download: Begin saving fanarts for ID {0}", albumId);
          _audioDb.DownloadImage(album.AlbumId, album.AlbumThumb, string.Format(@"{0}\{1}", FanArtScope.Album, FanArtType.Covers));
          _audioDb.DownloadImage(album.AlbumId, album.AlbumCDart, string.Format(@"{0}\{1}", FanArtScope.Album, FanArtType.DiscArt));
        }

        if (track.ArtistId.HasValue)
        {
          AudioDbArtist artist;
          if (!_audioDb.GetArtistFromId(track.ArtistId.Value, out artist))
            return;

          ServiceRegistration.Get<ILogger>().Debug("TheAudioDbMatcher Download: Begin saving artist banners for ID {0}", albumId);
          if (!string.IsNullOrEmpty(artist.ArtistBanner)) _audioDb.DownloadImage(artist.ArtistId, artist.ArtistBanner, string.Format(@"{0}\{1}", FanArtScope.Artist, FanArtType.Banners));
          if (!string.IsNullOrEmpty(artist.ArtistFanart)) _audioDb.DownloadImage(artist.ArtistId, artist.ArtistFanart, string.Format(@"{0}\{1}", FanArtScope.Artist, FanArtType.Backdrops));
          if (!string.IsNullOrEmpty(artist.ArtistFanart2)) _audioDb.DownloadImage(artist.ArtistId, artist.ArtistFanart2, string.Format(@"{0}\{1}", FanArtScope.Artist, FanArtType.Backdrops));
          if (!string.IsNullOrEmpty(artist.ArtistFanart3)) _audioDb.DownloadImage(artist.ArtistId, artist.ArtistFanart3, string.Format(@"{0}\{1}", FanArtScope.Artist, FanArtType.Backdrops));
          if (!string.IsNullOrEmpty(artist.ArtistLogo)) _audioDb.DownloadImage(artist.ArtistId, artist.ArtistLogo, string.Format(@"{0}\{1}", FanArtScope.Artist, FanArtType.Logos));
          if (!string.IsNullOrEmpty(artist.ArtistThumb)) _audioDb.DownloadImage(artist.ArtistId, artist.ArtistThumb, string.Format(@"{0}\{1}", FanArtScope.Artist, FanArtType.Thumbnails));
        }

        ServiceRegistration.Get<ILogger>().Debug("TheAudioDbMatcher Download: Finished ID {0}", albumId);

        // Remember we are finished
        FinishDownloadFanArt(albumId);
      }
      catch (Exception ex)
      {
        ServiceRegistration.Get<ILogger>().Debug("TheAudioDbMatcher: Exception downloading FanArt for ID {0}", ex, albumId);
      }
    }

    #endregion
  }
}
