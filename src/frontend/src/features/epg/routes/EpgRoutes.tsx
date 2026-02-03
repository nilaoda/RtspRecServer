import { lazy } from 'react'
import type { RouteObject } from 'react-router-dom'

import { EpgProvider } from '../context/EpgContext'

const EpgLayout = lazy(() => import('../layout/EpgLayout').then(module => ({ default: module.EpgLayout })))
const EpgOverviewPage = lazy(() => import('../pages/EpgOverviewPage'))
const EpgChannelsPage = lazy(() => import('../pages/EpgChannelsPage'))
const EpgChannelPage = lazy(() => import('../pages/EpgChannelPage'))
const EpgCurrentPage = lazy(() => import('../pages/EpgCurrentPage'))
const EpgCategoriesPage = lazy(() => import('../pages/EpgCategoriesPage'))
const EpgCategoryChannelsPage = lazy(() => import('../pages/EpgCategoryChannelsPage'))

export const epgRoutes: RouteObject[] = [
  {
    path: '/epg',
    element: (
      <EpgProvider>
        <EpgLayout />
      </EpgProvider>
    ),
    children: [
      { index: true, element: <EpgOverviewPage /> },
      { path: 'channels', element: <EpgChannelsPage /> },
      { path: 'channels/:channelId', element: <EpgChannelPage /> },
      { path: 'current', element: <EpgCurrentPage /> },
      { path: 'categories', element: <EpgCategoriesPage /> },
      { path: 'categories/:category', element: <EpgCategoryChannelsPage /> }
    ]
  }
]