﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MSDefragLib
{
    /// <summary>
    /// List in memory of all the files on disk, sorted
    /// by LCN (Logical Cluster Number).
    /// </summary>
    public class ItemStruct
    {
        /// <summary>
        /// Return the location on disk (LCN, Logical
        /// Cluster Number) of an item.
        /// </summary>
        public UInt64 Lcn
        {
            get
            {
                Fragment fragment = FragmentList.Fragments.
                    FirstOrDefault( x => x.Lcn == Fragment.VIRTUALFRAGMENT);
                if (fragment == null)
                    return 0;
                return fragment.Lcn;
            }
        }

        /// <summary>
        /// Return the number of fragments in the item.
        /// </summary>
        /// <param name="Item"></param>
        /// <returns></returns>
        public int FragmentCount
        {
            get
            {
                int count = 0;

                UInt64 Vcn = 0;
                UInt64 NextLcn = 0;

                foreach (Fragment fragment in FragmentList.Fragments)
                {
                    if (fragment.Lcn != Fragment.VIRTUALFRAGMENT)
                    {
                        if ((NextLcn != 0) && (fragment.Lcn != NextLcn))
                            count++;

                        NextLcn = fragment.Lcn + fragment.NextVcn - Vcn;
                    }

                    Vcn = fragment.NextVcn;
                }

                if (NextLcn != 0)
                    count++;

                return count;
            }
        }



        public ItemStruct Parent;              /* Parent item. */
        public ItemStruct Smaller;             /* Next smaller item. */
        public ItemStruct Bigger;              /* Next bigger item. */

        public String LongFilename;                /* Long filename. */
        public String LongPath;                    /* Full path on disk, long filenames. */
        public String ShortFilename;               /* Short filename (8.3 DOS). */
        public String ShortPath;                   /* Full path on disk, short filenames. */

        public UInt64 Bytes;                        /* Total number of bytes. */
        public UInt64 Clusters;                     /* Total number of clusters. */
        public UInt64 CreationTime;                 /* 1 second = 10000000 */
        public UInt64 MftChangeTime;
        public UInt64 LastAccessTime;

        /* List of fragments. */
        public FragmentList FragmentList { get; set; }

        public UInt64 ParentInode;                  /* The Inode number of the parent directory. */

        public ItemStruct ParentDirectory;

        public Boolean Directory;                    /* YES: it's a directory. */
        public Boolean Unmovable;                    /* YES: file can't/couldn't be moved. */
        public Boolean Exclude;                      /* YES: file is not to be defragged/optimized. */
        public Boolean SpaceHog;                     /* YES: file to be moved to end of disk. */
    };
}
