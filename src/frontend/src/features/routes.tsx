import { Route, Routes } from 'react-router-dom'
import TasksPage from './tasks/TasksPage'
import ChannelsPage from './channels/ChannelsPage'
import RecordingsPage from './recordings/RecordingsPage'
import SettingsPage from './settings/SettingsPage'
import { epgRoutes } from './epg'

const AppRoutes = () => {
  return (
    <Routes>
      <Route path="/" element={<TasksPage />} />
      <Route path="/channels" element={<ChannelsPage />} />
      <Route path="/recordings" element={<RecordingsPage />} />
      <Route path="/settings" element={<SettingsPage />} />
      {epgRoutes.map((route) => (
        <Route key={route.path} path={route.path} element={route.element}>
          {route.children?.map((child) => (
            <Route key={child.path || 'index'} index={child.index} path={child.path} element={child.element} />
          ))}
        </Route>
      ))}
    </Routes>
  )
}

export default AppRoutes