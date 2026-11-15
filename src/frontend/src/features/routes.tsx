import { Route, Routes } from 'react-router-dom'
import TasksPage from './tasks/TasksPage'
import ChannelsPage from './channels/ChannelsPage'
import M3uImportPage from './channels/M3uImportPage'
import RecordingsPage from './recordings/RecordingsPage'
import TimersPage from './timers/TimersPage'
import SettingsPage from './settings/SettingsPage'
import LogsPage from './log-viewer/LogsPage'
import { epgRoutes } from './epg'

const AppRoutes = () => {
  return (
    <Routes>
      <Route path="/" element={<TasksPage />} />
      <Route path="/channels" element={<ChannelsPage />} />
      <Route path="/channels/import" element={<M3uImportPage />} />
      <Route path="/recordings" element={<RecordingsPage />} />
      <Route path="/timers" element={<TimersPage />} />
      <Route path="/logs" element={<LogsPage />} />
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