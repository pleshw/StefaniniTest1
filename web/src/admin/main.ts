import 'bootstrap/dist/css/bootstrap.min.css';
import 'bootstrap-icons/font/bootstrap-icons.min.css';
import '../shared/ui/base.css';
import './ui/admin.css';
import { requireElement } from '../shared/ui/dom';
import { mountAdmin } from './ui/app';

mountAdmin(requireElement('app'));
